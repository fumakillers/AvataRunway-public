import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';
import {
    calculateOrbitControlsZoomScale,
    calculateVrmBodyTarget,
    FRAME_FILL_RATIO,
    isCameraResetShortcut,
    VrmCameraController
} from '../src/camera-controller.js';

const WHEEL_NOTCH_DELTA = 100;

function createController(aspect = 16 / 9) {
    const perspective = new THREE.PerspectiveCamera(35, aspect, 0.01, 1000);
    const orthographic = new THREE.OrthographicCamera(-aspect, aspect, 1, -1, 0.01, 1000);
    const controller = new VrmCameraController(perspective, orthographic);
    controller.resize(aspect);
    return controller;
}

function projectedFractions(controller, box) {
    const size = box.getSize(new THREE.Vector3());
    const halfFov = THREE.MathUtils.degToRad(controller.perspectiveCamera.fov) / 2;
    const frontDistance = controller.distance - size.z / 2;
    return {
        height: (size.y / 2) / (frontDistance * Math.tan(halfFov)),
        width: (size.x / 2) /
            (frontDistance * Math.tan(halfFov) * controller.perspectiveCamera.aspect)
    };
}

test('fitToBox frames a normal avatar at 80 percent height and faces front', () => {
    const controller = createController();
    const box = new THREE.Box3(
        new THREE.Vector3(-0.3, -0.1, -0.15),
        new THREE.Vector3(0.3, 1.9, 0.15)
    );

    controller.fitToBox(box);
    const fractions = projectedFractions(controller, box);

    assert.ok(Math.abs(fractions.height - FRAME_FILL_RATIO) < 1e-10);
    assert.ok(fractions.width <= FRAME_FILL_RATIO);
    assert.ok(controller.target.distanceTo(new THREE.Vector3(0, 0.9, 0)) < 1e-10);
    assert.ok(controller.baseTarget.distanceTo(new THREE.Vector3(0, 0.9, 0)) < 1e-10);
    assert.deepEqual(controller.panOffset.toArray(), [0, 0, 0]);
    assert.equal(controller.yaw, 0);
    assert.equal(controller.pitch, 0);
    assert.equal(controller.projection, 'perspective');
});

test('fitToBox accounts for a wide model and viewport aspect without cropping', () => {
    const controller = createController(0.75);
    const box = new THREE.Box3(
        new THREE.Vector3(-1.2, 0, -0.2),
        new THREE.Vector3(1.2, 1.8, 0.2)
    );

    controller.fitToBox(box);
    const fractions = projectedFractions(controller, box);

    assert.ok(fractions.height <= FRAME_FILL_RATIO);
    assert.ok(Math.abs(fractions.width - FRAME_FILL_RATIO) < 1e-10);
});

test('projection changes preserve target-plane display scale and camera state', () => {
    const controller = createController();
    const box = new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    );
    controller.fitToBox(box);
    controller.orbit(0.4, 0.2);
    const before = controller.snapshot();

    controller.toggleProjection();
    const orthographic = controller.snapshot();
    controller.toggleProjection();
    const perspective = controller.snapshot();

    assert.equal(orthographic.projection, 'orthographic');
    assert.ok(Math.abs(orthographic.visibleHalfHeight - before.visibleHalfHeight) < 1e-10);
    assert.ok(Math.abs(perspective.visibleHalfHeight - before.visibleHalfHeight) < 1e-10);
    assert.equal(perspective.yaw, before.yaw);
    assert.equal(perspective.pitch, before.pitch);
    assert.deepEqual(perspective.target, before.target);
});

test('deltaY -100 zooms in with the OrbitControls scale', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));
    const initialDistance = controller.distance;
    const expectedScale = Math.pow(0.95, Math.abs(-WHEEL_NOTCH_DELTA * 0.01));

    controller.zoom(-WHEEL_NOTCH_DELTA);

    assert.equal(calculateOrbitControlsZoomScale(-WHEEL_NOTCH_DELTA), expectedScale);
    assert.ok(Math.abs(controller.distance - initialDistance * expectedScale) < 1e-12);
});

test('deltaY 100 zooms out with the OrbitControls scale', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));
    const initialDistance = controller.distance;
    const expectedScale = Math.pow(0.95, Math.abs(WHEEL_NOTCH_DELTA * 0.01));

    controller.zoom(WHEEL_NOTCH_DELTA);

    assert.equal(calculateOrbitControlsZoomScale(WHEEL_NOTCH_DELTA), expectedScale);
    assert.ok(Math.abs(controller.distance - initialDistance / expectedScale) < 1e-12);
});

test('zoom amount follows the absolute deltaY magnitude', () => {
    const smallDeltaController = createController();
    const largeDeltaController = createController();
    const box = new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    );
    smallDeltaController.fitToBox(box);
    largeDeltaController.fitToBox(box);

    smallDeltaController.zoom(-10);
    largeDeltaController.zoom(-100);

    assert.ok(largeDeltaController.distance < smallDeltaController.distance);
});

test('perspective zoom can pass the former model-radius minimum distance', () => {
    const controller = createController();
    const box = new THREE.Box3(
        new THREE.Vector3(-0.5, 0, -0.5),
        new THREE.Vector3(0.5, 2, 0.5)
    );
    const legacyMinimum = box.getBoundingSphere(new THREE.Sphere()).radius * 0.75;
    controller.fitToBox(box);

    for (let index = 0; index < 100; index += 1) {
        controller.zoom(-WHEEL_NOTCH_DELTA);
    }

    assert.equal(controller.minDistance, 0);
    assert.equal(controller.maxDistance, Infinity);
    assert.ok(controller.distance < legacyMinimum);
});

test('orthographic zoom can exceed the former model-dependent maximum', () => {
    const controller = createController();
    const box = new THREE.Box3(
        new THREE.Vector3(-0.5, 0, -0.5),
        new THREE.Vector3(0.5, 2, 0.5)
    );
    const legacyMinimum = box.getBoundingSphere(new THREE.Sphere()).radius * 0.75;
    const halfFov = THREE.MathUtils.degToRad(controller.perspectiveCamera.fov) / 2;
    const legacyMaximumZoom = 1 / (legacyMinimum * Math.tan(halfFov));
    controller.fitToBox(box);
    controller.toggleProjection();

    for (let index = 0; index < 100; index += 1) {
        controller.zoom(-WHEEL_NOTCH_DELTA);
    }

    assert.equal(controller.minOrthographicZoom, 0);
    assert.equal(controller.maxOrthographicZoom, Infinity);
    assert.ok(controller.orthographicZoom > legacyMaximumZoom);
});

test('perspective and orthographic zoom can reverse back to their starting values', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));
    const perspectiveStart = controller.distance;

    controller.zoom(-WHEEL_NOTCH_DELTA);
    controller.zoom(WHEEL_NOTCH_DELTA);
    assert.ok(Math.abs(controller.distance - perspectiveStart) < 1e-12);

    controller.toggleProjection();
    const orthographicStart = controller.orthographicZoom;
    controller.zoom(-WHEEL_NOTCH_DELTA);
    controller.zoom(WHEEL_NOTCH_DELTA);
    assert.ok(Math.abs(controller.orthographicZoom - orthographicStart) < 1e-12);
});

test('very close perspective zoom keeps camera and clipping values finite', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));

    for (let index = 0; index < 10000; index += 1) {
        controller.zoom(-WHEEL_NOTCH_DELTA);
    }

    assert.ok(Number.isFinite(controller.distance));
    assert.ok(Number.isFinite(controller.perspectiveCamera.near));
    assert.ok(Number.isFinite(controller.perspectiveCamera.far));
    assert.ok(controller.perspectiveCamera.position.toArray().every(Number.isFinite));
    assert.ok(controller.perspectiveCamera.projectionMatrix.elements.every(Number.isFinite));
});

test('projection changes preserve display scale after an unrestricted zoom', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));
    for (let index = 0; index < 50; index += 1) {
        controller.zoom(-WHEEL_NOTCH_DELTA);
    }
    const perspectiveHalfHeight = controller.visibleHalfHeight;

    controller.toggleProjection();
    const orthographicHalfHeight = controller.visibleHalfHeight;
    controller.toggleProjection();

    assert.ok(Math.abs(orthographicHalfHeight - perspectiveHalfHeight) < 1e-12);
    assert.ok(Math.abs(controller.visibleHalfHeight - perspectiveHalfHeight) < 1e-12);
});

test('preset and incremental rotations preserve zoom and enforce pitch limits', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));
    const distance = controller.distance;

    controller.viewRightFront();
    assert.ok(Math.abs(controller.yaw - Math.PI / 4) < 1e-10);
    controller.flip();
    assert.ok(Math.abs(controller.yaw + Math.PI * 0.75) < 1e-10);
    controller.viewFront();
    assert.equal(controller.yaw, 0);
    assert.equal(controller.pitch, 0);
    controller.viewTop();
    assert.ok(Math.abs(controller.pitch - Math.PI / 2) < 1e-10);
    assert.ok(Math.abs(controller.camera.up.length() - 1) < 1e-10);
    controller.orbit(0, Math.PI);
    assert.ok(controller.pitch < Math.PI / 2);
    assert.equal(controller.distance, distance);
});

test('resize and zero-distance pan do not reset the current view', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));
    controller.orbit(0.3, -0.2);
    controller.zoom(-WHEEL_NOTCH_DELTA);
    const before = controller.snapshot();

    controller.resize(0.8);
    controller.panPixels(0, 0, 720);
    const after = controller.snapshot();

    assert.equal(after.yaw, before.yaw);
    assert.equal(after.pitch, before.pitch);
    assert.equal(after.distance, before.distance);
    assert.equal(after.visibleHalfHeight, before.visibleHalfHeight);
    assert.deepEqual(after.target, before.target);
    assert.deepEqual(after.panOffset, before.panOffset);
});

test('pan tracks an explicit offset while moving the look-at target', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));
    controller.zoom(-WHEEL_NOTCH_DELTA);
    const originalTarget = controller.target.clone();

    controller.panPixels(40, -20, 720);

    assert.ok(controller.panOffset.length() > 0);
    assert.ok(controller.target.clone().sub(originalTarget).distanceTo(controller.panOffset) < 1e-10);
});

test('body target uses hips X/Z and vertical bounds instead of an asymmetric box center', () => {
    const hips = new THREE.Object3D();
    hips.position.set(0.15, 0.85, -0.12);
    hips.updateWorldMatrix(true, false);
    const vrm = {
        humanoid: {
            getRawBoneNode: (boneName) => boneName === 'hips' ? hips : null
        }
    };
    const asymmetricBox = new THREE.Box3(
        new THREE.Vector3(-0.8, -0.1, -0.5),
        new THREE.Vector3(2.4, 2.1, 0.9)
    );

    const target = calculateVrmBodyTarget(vrm, asymmetricBox);

    assert.ok(target.distanceTo(new THREE.Vector3(0.15, 1, -0.12)) < 1e-10);
    assert.notEqual(target.x, asymmetricBox.getCenter(new THREE.Vector3()).x);
    assert.notEqual(target.z, asymmetricBox.getCenter(new THREE.Vector3()).z);
});

test('body target falls back to the bounding box center without humanoid torso bones', () => {
    const box = new THREE.Box3(
        new THREE.Vector3(-0.8, -0.1, -0.5),
        new THREE.Vector3(2.4, 2.1, 0.9)
    );

    const target = calculateVrmBodyTarget({ humanoid: null }, box);

    assert.ok(target.distanceTo(box.getCenter(new THREE.Vector3())) < 1e-10);
});

test('body target uses head and feet to choose the vertical rotation center', () => {
    const bones = {
        hips: new THREE.Object3D(),
        upperChest: new THREE.Object3D(),
        head: new THREE.Object3D(),
        leftFoot: new THREE.Object3D(),
        rightFoot: new THREE.Object3D()
    };
    bones.hips.position.set(0.1, 0.9, -0.1);
    bones.upperChest.position.set(0.1, 1.4, -0.1);
    bones.head.position.set(0.1, 1.7, -0.1);
    bones.leftFoot.position.set(-0.1, 0.1, 0);
    bones.rightFoot.position.set(0.1, 0.1, 0);
    Object.values(bones).forEach((bone) => bone.updateWorldMatrix(true, false));
    const vrm = {
        humanoid: {
            getRawBoneNode: (boneName) => bones[boneName] ?? null
        }
    };
    const box = new THREE.Box3(
        new THREE.Vector3(-1, 0, -0.5),
        new THREE.Vector3(1, 2, 0.5)
    );

    const target = calculateVrmBodyTarget(vrm, box);

    assert.ok(Math.abs(target.y - 0.975) < 1e-10);
});

test('orbit keeps the body target centered through 360 degrees in both projections', () => {
    const controller = createController();
    const box = new THREE.Box3(
        new THREE.Vector3(-0.8, -0.1, -0.5),
        new THREE.Vector3(2.4, 2.1, 0.9)
    );
    const bodyTarget = new THREE.Vector3(0.15, 1, -0.12);
    controller.fitToBox(box, bodyTarget);
    controller.panPixels(30, -20, 720);
    const expectedTarget = controller.baseTarget.clone().add(controller.panOffset);
    const initialDistance = controller.distance;
    const initialPosition = controller.camera.position.clone();

    for (const projection of ['perspective', 'orthographic']) {
        if (controller.projection !== projection) {
            controller.toggleProjection();
        }
        for (const yaw of [0, Math.PI / 4, Math.PI / 2, Math.PI, Math.PI * 1.5, Math.PI * 2]) {
            controller.orbit(yaw - controller.yaw, 0);
            const projectedTarget = controller.target.clone().project(controller.camera);
            assert.ok(Math.abs(projectedTarget.x) < 1e-10);
            assert.ok(Math.abs(projectedTarget.y) < 1e-10);
            assert.ok(controller.target.distanceTo(expectedTarget) < 1e-10);
        }
    }

    if (controller.projection !== 'perspective') {
        controller.toggleProjection();
    }
    controller.viewFront();
    assert.ok(controller.camera.position.distanceTo(initialPosition) < 1e-10);
    assert.equal(controller.distance, initialDistance);
});

test('reset restores the complete initial camera state using the current aspect ratio', () => {
    const box = new THREE.Box3(
        new THREE.Vector3(-0.8, -0.1, -0.5),
        new THREE.Vector3(2.4, 2.1, 0.9)
    );
    const bodyTarget = new THREE.Vector3(0.15, 1, -0.12);
    const controller = createController(16 / 9);
    controller.fitToBox(box, bodyTarget);
    const loadTimeDistance = controller.initialState.distance;

    controller.panPixels(80, -35, 720);
    controller.orbit(0.8, -0.35);
    for (let index = 0; index < 200; index += 1) {
        controller.zoom(-WHEEL_NOTCH_DELTA);
    }
    controller.toggleProjection();
    for (let index = 0; index < 200; index += 1) {
        controller.zoom(-WHEEL_NOTCH_DELTA);
    }
    controller.viewTop();
    controller.resize(0.65);

    const expected = createController(0.65);
    expected.fitToBox(box, bodyTarget);
    assert.equal(controller.resetToInitialView(), true);

    assert.ok(controller.baseTarget.distanceTo(expected.baseTarget) < 1e-10);
    assert.deepEqual(controller.panOffset.toArray(), [0, 0, 0]);
    assert.ok(controller.target.distanceTo(bodyTarget) < 1e-10);
    assert.equal(controller.yaw, 0);
    assert.equal(controller.pitch, 0);
    assert.equal(controller.projection, 'perspective');
    assert.ok(Math.abs(controller.distance - expected.distance) < 1e-10);
    assert.ok(Math.abs(controller.orthographicZoom - expected.orthographicZoom) < 1e-10);
    assert.ok(controller.camera.up.distanceTo(new THREE.Vector3(0, 1, 0)) < 1e-10);
    assert.notEqual(controller.distance, loadTimeDistance);
});

test('front view preserves pan, zoom, and projection while reset clears them', () => {
    const controller = createController();
    controller.fitToBox(new THREE.Box3(
        new THREE.Vector3(-0.3, 0, -0.15),
        new THREE.Vector3(0.3, 1.8, 0.15)
    ));
    controller.panPixels(40, -20, 720);
    controller.zoom(-WHEEL_NOTCH_DELTA);
    controller.toggleProjection();
    controller.orbit(0.7, 0.3);
    const changed = controller.snapshot();

    controller.viewFront();
    const front = controller.snapshot();

    assert.deepEqual(front.target, changed.target);
    assert.deepEqual(front.panOffset, changed.panOffset);
    assert.equal(front.distance, changed.distance);
    assert.equal(front.orthographicZoom, changed.orthographicZoom);
    assert.equal(front.projection, changed.projection);
    assert.equal(front.yaw, 0);
    assert.equal(front.pitch, 0);

    controller.resetToInitialView();
    assert.deepEqual(controller.panOffset.toArray(), [0, 0, 0]);
    assert.equal(controller.projection, 'perspective');
});

test('reset is unavailable before a model has established its initial framing', () => {
    assert.equal(createController().resetToInitialView(), false);
});

test('R reset shortcut accepts either case and rejects modifiers or repeats', () => {
    const event = {
        code: 'KeyR',
        ctrlKey: false,
        altKey: false,
        metaKey: false,
        repeat: false
    };

    assert.equal(isCameraResetShortcut(event), true);
    assert.equal(isCameraResetShortcut({ ...event, shiftKey: true }), true);
    assert.equal(isCameraResetShortcut({ ...event, ctrlKey: true }), false);
    assert.equal(isCameraResetShortcut({ ...event, altKey: true }), false);
    assert.equal(isCameraResetShortcut({ ...event, metaKey: true }), false);
    assert.equal(isCameraResetShortcut({ ...event, repeat: true }), false);
});
