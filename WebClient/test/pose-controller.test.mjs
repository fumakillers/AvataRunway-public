import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';
import {
    deltaToLocal,
    eulerDegreesToQuaternion,
    neutralToDelta,
    PoseController,
    quaternionToEulerDegrees
} from '../src/pose-controller.js';
import { FINGER_NAMES, fingerBoneNames } from '../src/hand-pose.js';

globalThis.requestAnimationFrame ??= (callback) => {
    callback();
    return 1;
};
globalThis.cancelAnimationFrame ??= () => {};

function equivalentRotation(left, right, tolerance = 1e-10) {
    return 1 - Math.abs(left.dot(right)) < tolerance;
}

function createCameraAndDom() {
    const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 1000);
    camera.position.set(0, 0, 5);
    camera.lookAt(0, 0, 0);
    camera.updateProjectionMatrix();
    camera.updateMatrixWorld(true);
    return {
        camera,
        domElement: {
            getBoundingClientRect: () => ({ left: 0, top: 0, width: 720, height: 720 })
        }
    };
}

function clientPoint(position, camera) {
    const projected = position.clone().project(camera);
    return {
        clientX: (projected.x + 1) * 360,
        clientY: (1 - projected.y) * 360
    };
}

function createVrm(scene, bones) {
    const vrmScene = new THREE.Object3D();
    scene.add(vrmScene);
    for (const bone of Object.values(bones)) {
        if (!bone.parent) {
            vrmScene.add(bone);
        }
    }
    return {
        scene: vrmScene,
        humanoid: {
            humanBones: Object.fromEntries(Object.keys(bones).map((name) => [name, { node: bones[name] }])),
            getNormalizedBoneNode: (name) => bones[name] ?? null
        }
    };
}

function createHumanoidVrm(scene, includeUpperChest = true) {
    const normalizedRoot = new THREE.Object3D();
    const bones = {};
    const addBone = (name, parent, position) => {
        const bone = new THREE.Object3D();
        bone.name = name;
        bone.position.fromArray(position);
        parent.add(bone);
        bones[name] = bone;
        return bone;
    };
    const hips = addBone('hips', normalizedRoot, [0, 1.7, 0]);
    const spine = addBone('spine', hips, [0, 0.35, 0]);
    const chest = addBone('chest', spine, [0, 0.35, 0]);
    const torsoParent = includeUpperChest
        ? addBone('upperChest', chest, [0, 0.3, 0])
        : chest;
    const neck = addBone('neck', torsoParent, [0, 0.28, 0]);
    addBone('head', neck, [0, 0.28, 0]);

    const leftUpperArm = addBone('leftUpperArm', torsoParent, [-0.35, 0.1, 0]);
    const leftLowerArm = addBone('leftLowerArm', leftUpperArm, [-0.4, -0.15, 0.2]);
    addBone('leftHand', leftLowerArm, [-0.3, 0.15, -0.2]);
    const rightUpperArm = addBone('rightUpperArm', torsoParent, [0.35, 0.1, 0]);
    const rightLowerArm = addBone('rightLowerArm', rightUpperArm, [0.4, -0.15, 0.2]);
    addBone('rightHand', rightLowerArm, [0.3, 0.15, -0.2]);

    const leftUpperLeg = addBone('leftUpperLeg', hips, [-0.2, -0.05, 0]);
    const leftLowerLeg = addBone('leftLowerLeg', leftUpperLeg, [0, -0.75, 0.1]);
    addBone('leftFoot', leftLowerLeg, [0, -0.72, -0.1]);
    const rightUpperLeg = addBone('rightUpperLeg', hips, [0.2, -0.05, 0]);
    const rightLowerLeg = addBone('rightLowerLeg', rightUpperLeg, [0, -0.75, 0.1]);
    addBone('rightFoot', rightLowerLeg, [0, -0.72, -0.1]);

    const vrmScene = new THREE.Object3D();
    vrmScene.add(normalizedRoot);
    scene.add(vrmScene);
    normalizedRoot.updateWorldMatrix(true, true);
    return {
        bones,
        vrm: {
            scene: vrmScene,
            humanoid: {
                humanBones: Object.fromEntries(
                    Object.keys(bones).map((name) => [name, { node: bones[name] }])
                ),
                normalizedHumanBonesRoot: normalizedRoot,
                getNormalizedBoneNode: (name) => bones[name] ?? null
            }
        }
    };
}

function addRealisticFingerBones(vrm, bones, missing = new Set()) {
    const root = vrm.humanoid.normalizedHumanBonesRoot;
    root.quaternion.setFromEuler(new THREE.Euler(0.24, -0.31, 0.17));
    const rootOffsets = {
        thumb: -0.052,
        index: -0.034,
        middle: -0.011,
        ring: 0.013,
        little: 0.038
    };
    for (const side of ['left', 'right']) {
        const direction = side === 'left' ? -1 : 1;
        for (const finger of FINGER_NAMES) {
            let parent = bones[`${side}Hand`];
            for (const [index, name] of fingerBoneNames(side, finger).entries()) {
                if (missing.has(name)) {
                    continue;
                }
                const joint = new THREE.Object3D();
                joint.name = name;
                joint.position.set(
                    direction * (index === 0 ? 0.065 : finger === 'thumb' ? 0.05 : 0.075),
                    finger === 'thumb' ? -0.012 : 0,
                    index === 0 ? rootOffsets[finger] : finger === 'thumb' ? -0.018 : 0
                );
                joint.quaternion.setFromEuler(new THREE.Euler(
                    0.025 * (index + 1),
                    direction * 0.018 * (index + 1),
                    -0.012 * (index + 1)
                ));
                parent.add(joint);
                parent = joint;
                bones[name] = joint;
                vrm.humanoid.humanBones[name] = { node: joint };
            }
        }
    }
    root.updateWorldMatrix(true, true);
}

test('neutral rotation and delta rotation convert in both directions', () => {
    const neutral = new THREE.Quaternion().setFromEuler(new THREE.Euler(0.2, -0.4, 0.1, 'XYZ'));
    const delta = new THREE.Quaternion().setFromEuler(new THREE.Euler(-0.3, 0.25, 0.5, 'XYZ'));
    const current = deltaToLocal(neutral, delta);
    const recovered = neutralToDelta(neutral, current);
    assert.ok(equivalentRotation(recovered, delta));
});

test('Euler degree display uses XYZ and round-trips through quaternion', () => {
    const quaternion = eulerDegreesToQuaternion(25, -40, 70);
    const euler = quaternionToEulerDegrees(quaternion);
    assert.ok(Math.abs(euler.x - 25) < 1e-10);
    assert.ok(Math.abs(euler.y + 40) < 1e-10);
    assert.ok(Math.abs(euler.z - 70) < 1e-10);
    assert.ok(equivalentRotation(eulerDegreesToQuaternion(euler.x, euler.y, euler.z), quaternion));
});

test('rotation conversion rejects non-finite values', () => {
    assert.throws(() => eulerDegreesToQuaternion(Number.NaN, 0, 0), TypeError);
    assert.throws(
        () => neutralToDelta(new THREE.Quaternion(), new THREE.Quaternion(Number.POSITIVE_INFINITY, 0, 0, 1)),
        TypeError
    );
});

test('creates four always-on-top visual handles and four larger transparent hit targets', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});

    assert.equal(controller.handles.size, 4);
    assert.equal(controller.handleHitTargets.size, 4);
    for (const [endName, handle] of controller.handles) {
        const hitTarget = controller.handleHitTargets.get(endName);
        assert.equal(handle.material.transparent, true);
        assert.equal(handle.material.opacity, 1);
        assert.equal(handle.material.depthTest, false);
        assert.equal(handle.material.depthWrite, false);
        assert.equal(handle.material.toneMapped, false);
        assert.ok(handle.renderOrder >= 100000);
        assert.equal(hitTarget.material.depthTest, false);
        assert.equal(hitTarget.material.depthWrite, false);
        assert.equal(hitTarget.material.colorWrite, false);
        assert.equal(hitTarget.material.opacity, 0);
        assert.ok(hitTarget.renderOrder > handle.renderOrder);
    }
    assert.equal(controller.bendHandle.material.depthTest, false);
    assert.equal(controller.bendHandle.material.depthWrite, false);
    assert.equal(controller.bendHandle.material.opacity, 1);
    assert.ok(controller.bendHandle.renderOrder > 100000);
    assert.equal(controller.bendHitTarget.material.depthTest, false);
    assert.equal(controller.bendHitTarget.material.depthWrite, false);
    assert.equal(controller.bendHitTarget.material.colorWrite, false);
    assert.equal(controller.bendHitTarget.material.opacity, 0);
    assert.ok(controller.bendHitTarget.renderOrder > controller.bendHandle.renderOrder);

    controller.dispose();
});

test('keeps visual and hit-target screen radii stable in both camera projections', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    controller.handleGroup.visible = true;
    controller.bendHandle.visible = true;
    controller.bendHitTarget.visible = true;
    for (const [name, handle] of controller.trunkHandles) {
        handle.visible = true;
        controller.trunkHitTargets.get(name).visible = true;
    }
    for (const [boneName, marker] of controller.bodyMarkers) {
        marker.visible = true;
        controller.bodyMarkerHitTargets.get(boneName).visible = true;
    }
    const viewportHeight = 720;

    const perspective = new THREE.PerspectiveCamera(35, 1, 0.01, 1000);
    perspective.position.set(0, 0, 5);
    perspective.lookAt(0, 0, 0);
    perspective.updateMatrixWorld(true);
    controller.updateHandleScale(perspective, viewportHeight);
    const perspectiveWorldPerPixel =
        2 * 5 * Math.tan(THREE.MathUtils.degToRad(35) / 2) / viewportHeight;
    for (const [endName, handle] of controller.handles) {
        assert.ok(Math.abs(handle.scale.x / perspectiveWorldPerPixel - 11) < 1e-10);
        assert.ok(Math.abs(
            controller.handleHitTargets.get(endName).scale.x / perspectiveWorldPerPixel - 18
        ) < 1e-10);
    }
    assert.ok(Math.abs(
        controller.bendHandle.scale.x / perspectiveWorldPerPixel - 24
    ) < 1e-10);
    assert.ok(Math.abs(
        controller.bendHitTarget.scale.x / perspectiveWorldPerPixel - 27
    ) < 1e-10);
    for (const [name, handle] of controller.trunkHandles) {
        assert.ok(Math.abs(handle.scale.x / perspectiveWorldPerPixel - 13) < 1e-10);
        assert.ok(Math.abs(
            controller.trunkHitTargets.get(name).scale.x / perspectiveWorldPerPixel - 21
        ) < 1e-10);
    }
    for (const [boneName, marker] of controller.bodyMarkers) {
        assert.ok(Math.abs(marker.scale.x / perspectiveWorldPerPixel - 6) < 1e-10);
        assert.ok(Math.abs(
            controller.bodyMarkerHitTargets.get(boneName).scale.x /
                perspectiveWorldPerPixel - 13
        ) < 1e-10);
    }

    const orthographic = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.01, 1000);
    orthographic.position.set(0, 0, 5);
    orthographic.zoom = 2;
    orthographic.updateProjectionMatrix();
    orthographic.updateMatrixWorld(true);
    controller.updateHandleScale(orthographic, viewportHeight);
    const orthographicWorldPerPixel = 2 / (2 * viewportHeight);
    for (const [endName, handle] of controller.handles) {
        assert.ok(Math.abs(handle.scale.x / orthographicWorldPerPixel - 11) < 1e-10);
        assert.ok(Math.abs(
            controller.handleHitTargets.get(endName).scale.x / orthographicWorldPerPixel - 18
        ) < 1e-10);
    }
    assert.ok(Math.abs(
        controller.bendHandle.scale.x / orthographicWorldPerPixel - 24
    ) < 1e-10);
    assert.ok(Math.abs(
        controller.bendHitTarget.scale.x / orthographicWorldPerPixel - 27
    ) < 1e-10);
    for (const [name, handle] of controller.trunkHandles) {
        assert.ok(Math.abs(handle.scale.x / orthographicWorldPerPixel - 13) < 1e-10);
        assert.ok(Math.abs(
            controller.trunkHitTargets.get(name).scale.x / orthographicWorldPerPixel - 21
        ) < 1e-10);
    }
    for (const [boneName, marker] of controller.bodyMarkers) {
        assert.ok(Math.abs(marker.scale.x / orthographicWorldPerPixel - 6) < 1e-10);
        assert.ok(Math.abs(
            controller.bodyMarkerHitTargets.get(boneName).scale.x /
                orthographicWorldPerPixel - 13
        ) < 1e-10);
    }

    controller.dispose();
});

test('transparent hit target starts IK outside the visible sphere without model raycasts', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const vrmScene = new THREE.Object3D();
    scene.add(vrmScene);
    const positions = {
        leftUpperArm: [-2, 0, 0],
        leftLowerArm: [-1, 0, 0],
        leftHand: [0, 0, 0],
        rightUpperArm: [2, 3, 0],
        rightLowerArm: [3, 3, 0],
        rightHand: [4, 3, 0],
        leftUpperLeg: [-3, -2, 0],
        leftLowerLeg: [-3, -3, 0],
        leftFoot: [-3, -4, 0],
        rightUpperLeg: [3, -2, 0],
        rightLowerLeg: [3, -3, 0],
        rightFoot: [3, -4, 0]
    };
    const bones = {};
    for (const [name, position] of Object.entries(positions)) {
        const bone = new THREE.Object3D();
        bone.position.fromArray(position);
        vrmScene.add(bone);
        bones[name] = bone;
    }
    const vrm = {
        scene: vrmScene,
        humanoid: {
            humanBones: Object.fromEntries(Object.keys(bones).map((name) => [name, { node: bones[name] }])),
            getNormalizedBoneNode: (name) => bones[name] ?? null
        }
    };
    controller.setVrm(vrm);
    controller.setPoseMode(true);

    const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 1000);
    camera.position.set(0, 0, 5);
    camera.lookAt(0, 0, 0);
    camera.updateProjectionMatrix();
    camera.updateMatrixWorld(true);
    controller.updateHandleScale(camera, 720);
    const domElement = {
        getBoundingClientRect: () => ({ left: 0, top: 0, width: 720, height: 720 })
    };

    // 14 px from center: outside the 11 px visible sphere, inside the 18 px hit sphere.
    const started = controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        clientX: 374,
        clientY: 360,
        pointerId: 7
    }, camera, domElement);

    assert.equal(started, true);
    assert.equal(controller.drag.endName, 'leftHand');
    controller.dispose();
});

test('shows the matching bend handle after IK selection and hides it after empty selection', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const positions = {
        leftUpperArm: [-0.9, 0.5, 0],
        leftLowerArm: [-0.6, 0.5, 0],
        leftHand: [-0.3, 0.5, 0],
        rightUpperArm: [0.9, 0.5, 0],
        rightLowerArm: [0.6, 0.5, 0],
        rightHand: [0.3, 0.5, 0],
        leftUpperLeg: [-0.5, -0.2, 0],
        leftLowerLeg: [-0.5, -0.5, 0],
        leftFoot: [-0.5, -0.8, 0],
        rightUpperLeg: [0.5, -0.2, 0],
        rightLowerLeg: [0.5, -0.5, 0],
        rightFoot: [0.5, -0.8, 0]
    };
    const bones = {};
    for (const [name, position] of Object.entries(positions)) {
        bones[name] = new THREE.Object3D();
        bones[name].position.fromArray(position);
    }
    controller.setVrm(createVrm(scene, bones));
    controller.setPoseMode(true);

    const { camera, domElement } = createCameraAndDom();
    const expectedJoints = {
        leftHand: 'leftLowerArm',
        rightHand: 'rightLowerArm',
        leftFoot: 'leftLowerLeg',
        rightFoot: 'rightLowerLeg'
    };
    let pointerId = 10;
    for (const [endName, jointName] of Object.entries(expectedJoints)) {
        controller.updateHandleScale(camera, 720);
        scene.updateMatrixWorld(true);
        const handlePoint = clientPoint(
            bones[endName].getWorldPosition(new THREE.Vector3()),
            camera
        );
        pointerId += 1;
        assert.equal(controller.beginHandleDrag({
            button: 0,
            shiftKey: false,
            ...handlePoint,
            pointerId
        }, camera, domElement), true);
        controller.endHandleDrag(pointerId);

        assert.equal(controller.selectedLimb, endName);
        assert.equal(controller.selectedBone, jointName);
        assert.equal(controller.bendHandle.visible, true);
        assert.ok(controller.bendHandle.position.distanceTo(
            bones[jointName].getWorldPosition(new THREE.Vector3())
        ) < 1e-10);
    }

    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        clientX: 10,
        clientY: 10,
        pointerId: 12
    }, camera, domElement), false);
    assert.equal(controller.selectedLimb, '');
    assert.equal(controller.bendHandle.visible, false);
    controller.dispose();
});

test('bend-handle drag changes pole direction while preserving effector and bone lengths', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const root = new THREE.Object3D();
    const joint = new THREE.Object3D();
    const end = new THREE.Object3D();
    root.position.set(-1, 0, 0);
    joint.position.set(1, 0.5, 0);
    end.position.set(1, -0.5, 0);
    root.add(joint);
    joint.add(end);
    const bones = {
        leftUpperArm: root,
        leftLowerArm: joint,
        leftHand: end
    };
    controller.setVrm(createVrm(scene, bones));
    controller.setPoseMode(true);

    const { camera, domElement } = createCameraAndDom();
    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);
    const handPoint = clientPoint(end.getWorldPosition(new THREE.Vector3()), camera);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...handPoint,
        pointerId: 21
    }, camera, domElement), true);
    controller.endHandleDrag(21);
    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);

    const originalEffector = end.getWorldPosition(new THREE.Vector3());
    const upperLength = root.getWorldPosition(new THREE.Vector3())
        .distanceTo(joint.getWorldPosition(new THREE.Vector3()));
    const lowerLength = joint.getWorldPosition(new THREE.Vector3())
        .distanceTo(originalEffector);
    const bendPoint = clientPoint(joint.getWorldPosition(new THREE.Vector3()), camera);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...bendPoint,
        pointerId: 22
    }, camera, domElement), true);
    assert.equal(controller.drag.kind, 'bend');
    controller.moveHandleDrag({
        ...bendPoint,
        clientY: bendPoint.clientY + 180,
        pointerId: 22
    }, camera, domElement);
    controller.endHandleDrag(22);

    const solvedRoot = root.getWorldPosition(new THREE.Vector3());
    const solvedJoint = joint.getWorldPosition(new THREE.Vector3());
    const solvedEnd = end.getWorldPosition(new THREE.Vector3());
    assert.ok(solvedEnd.distanceTo(originalEffector) < 1e-5);
    assert.ok(Math.abs(solvedRoot.distanceTo(solvedJoint) - upperLength) < 1e-8);
    assert.ok(Math.abs(solvedJoint.distanceTo(solvedEnd) - lowerLength) < 1e-8);
    assert.ok(controller.explicitBendDirections.get('leftHand').y < 0);

    const pose = controller.getCurrentPose();
    assert.equal('poleTarget' in pose, false);
    assert.equal('bendDirections' in pose, false);
    controller.explicitBendDirections.clear();
    controller.applyPose(pose);
    assert.equal(controller.explicitBendDirections.size, 1);
    const restoredBend = controller.explicitBendDirections.get('leftHand').clone();

    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);
    controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        clientX: 10,
        clientY: 10,
        pointerId: 23
    }, camera, domElement);
    const restoredHandPoint = clientPoint(end.getWorldPosition(new THREE.Vector3()), camera);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...restoredHandPoint,
        pointerId: 24
    }, camera, domElement), true);
    assert.equal(controller.drag.kind, 'effector');
    assert.ok(controller.drag.preferredBend.dot(restoredBend) > 0.999);
    controller.moveHandleDrag({
        ...restoredHandPoint,
        clientX: restoredHandPoint.clientX + 20,
        pointerId: 24
    }, camera, domElement);
    controller.endHandleDrag(24);
    assert.ok(controller.currentBendDirection('leftHand').dot(restoredBend) > 0);

    controller.resetAll();
    assert.equal(controller.explicitBendDirections.size, 0);
    assert.equal(controller.selectedLimb, '');
    assert.equal(controller.bendHandle.visible, false);
    controller.dispose();
});

test('creates distinct always-on-top hips and neck handles with larger hit targets', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    assert.equal(controller.trunkHandles.size, 2);
    for (const [name, handle] of controller.trunkHandles) {
        const hitTarget = controller.trunkHitTargets.get(name);
        assert.equal(handle.material.depthTest, false);
        assert.equal(handle.material.depthWrite, false);
        assert.ok(handle.renderOrder > controller.bendHandle.renderOrder);
        assert.equal(hitTarget.material.depthTest, false);
        assert.equal(hitTarget.material.depthWrite, false);
        assert.equal(hitTarget.material.colorWrite, false);
        assert.ok(hitTarget.renderOrder > handle.renderOrder);
    }
    assert.notEqual(
        controller.trunkHandles.get('hips').material.color.getHex(),
        controller.trunkHandles.get('neck').material.color.getHex()
    );
    controller.dispose();
});

test('creates amber three-axis wrist rings with always-on-top raycast geometry', () => {
    const controller = new PoseController(new THREE.Scene(), () => {});
    assert.equal(controller.wristHandles.size, 2);
    for (const [endName, group] of controller.wristHandles) {
        const visuals = group.children.filter((child) => child.material?.colorWrite !== false);
        const hits = controller.wristHitTargets.get(endName);
        assert.equal(visuals.length, 3);
        assert.equal(hits.length, 3);
        assert.ok(visuals.every((ring) =>
            ring.material.color.getHex() === 0xffb020 &&
            ring.material.depthTest === false &&
            ring.material.depthWrite === false &&
            ring.renderOrder > controller.trunkHandles.get('neck').renderOrder));
        assert.ok(hits.every((hit) =>
            hit.material.colorWrite === false &&
            hit.material.depthTest === false &&
            hit.material.depthWrite === false));
    }
    controller.dispose();
});

test('hips drag preserves both foot targets and round-trips normalized translation', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const { vrm, bones } = createHumanoidVrm(scene);
    controller.setVrm(vrm);
    controller.setPoseMode(true);

    const { camera, domElement } = createCameraAndDom();
    camera.position.set(0, 1.5, 5);
    camera.lookAt(0, 1.5, 0);
    camera.updateMatrixWorld(true);
    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);

    const leftFootTarget = bones.leftFoot.getWorldPosition(new THREE.Vector3());
    const rightFootTarget = bones.rightFoot.getWorldPosition(new THREE.Vector3());
    const leftHandTarget = bones.leftHand.getWorldPosition(new THREE.Vector3());
    const rightHandTarget = bones.rightHand.getWorldPosition(new THREE.Vector3());
    controller.explicitEffectorTargets.set('leftHand', leftHandTarget.clone());
    controller.explicitEffectorTargets.set('rightHand', rightHandTarget.clone());
    const hipsPoint = clientPoint(bones.hips.getWorldPosition(new THREE.Vector3()), camera);
    controller.updateHover({ ...hipsPoint }, camera, domElement);
    assert.equal(controller.hoveredTrunk, 'hips');
    assert.equal(controller.trunkHandles.get('hips').material.color.getHex(), 0xffff66);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...hipsPoint,
        pointerId: 31
    }, camera, domElement), true);
    assert.equal(controller.drag.kind, 'hips');
    assert.equal(controller.selectedBone, 'hips');
    controller.moveHandleDrag({
        ...hipsPoint,
        clientY: hipsPoint.clientY + 35,
        pointerId: 31
    }, camera, domElement);
    controller.endHandleDrag(31);

    assert.ok(bones.leftFoot.getWorldPosition(new THREE.Vector3()).distanceTo(leftFootTarget) < 1e-4);
    assert.ok(bones.rightFoot.getWorldPosition(new THREE.Vector3()).distanceTo(rightFootTarget) < 1e-4);
    assert.ok(bones.leftHand.getWorldPosition(new THREE.Vector3()).distanceTo(leftHandTarget) < 1e-4);
    assert.ok(bones.rightHand.getWorldPosition(new THREE.Vector3()).distanceTo(rightHandTarget) < 1e-4);
    const exported = controller.getCurrentPose();
    assert.equal(exported.version, 2);
    assert.equal(exported.translationSpace, 'normalized-hips-local-height-ratio');
    assert.ok(Math.abs(exported.hipsTranslation.y) > 1e-5);
    const translatedPosition = bones.hips.position.clone();

    controller.resetAll();
    assert.ok(bones.hips.position.distanceTo(controller.neutralHipsPosition) < 1e-10);
    controller.applyPose(exported);
    assert.ok(bones.hips.position.distanceTo(translatedPosition) < 1e-10);
    controller.resetBone('hips');
    assert.ok(bones.hips.position.distanceTo(controller.neutralHipsPosition) < 1e-10);
    assert.deepEqual(controller.getCurrentPose().hipsTranslation, { x: 0, y: 0, z: 0 });

    const legacy = structuredClone(exported);
    legacy.version = 1;
    delete legacy.translationSpace;
    delete legacy.hipsTranslation;
    controller.applyPose(legacy);
    assert.ok(bones.hips.position.distanceTo(controller.neutralHipsPosition) < 1e-10);
    controller.dispose();
});

test('hips solving respects the legs minimum reach and keeps both feet fixed', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const { vrm, bones } = createHumanoidVrm(scene);
    controller.setVrm(vrm);
    controller.setPoseMode(true);
    const { camera, domElement } = createCameraAndDom();
    camera.position.set(0, 1.5, 5);
    camera.lookAt(0, 1.5, 0);
    camera.updateMatrixWorld(true);
    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);

    const footTargets = {
        leftFoot: bones.leftFoot.getWorldPosition(new THREE.Vector3()),
        rightFoot: bones.rightFoot.getWorldPosition(new THREE.Vector3())
    };
    const hipsPoint = clientPoint(bones.hips.getWorldPosition(new THREE.Vector3()), camera);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...hipsPoint,
        pointerId: 35
    }, camera, domElement), true);
    const leftConstraint = controller.drag.pelvisConstraints[0];
    const requested = leftConstraint.target.clone().sub(leftConstraint.rootOffset);
    assert.equal(controller.solveHipsTarget(requested, controller.drag), true);

    for (const endName of ['leftFoot', 'rightFoot']) {
        const limb = {
            leftFoot: ['leftUpperLeg', 'leftLowerLeg', 'leftFoot'],
            rightFoot: ['rightUpperLeg', 'rightLowerLeg', 'rightFoot']
        }[endName];
        const rootPosition = bones[limb[0]].getWorldPosition(new THREE.Vector3());
        const jointPosition = bones[limb[1]].getWorldPosition(new THREE.Vector3());
        const endPosition = bones[limb[2]].getWorldPosition(new THREE.Vector3());
        const minimumLength = Math.abs(
            rootPosition.distanceTo(jointPosition) -
            jointPosition.distanceTo(endPosition)
        );
        assert.ok(rootPosition.distanceTo(footTargets[endName]) >= minimumLength - 1e-5);
        assert.ok(endPosition.distanceTo(footTargets[endName]) < 1e-4);
    }
    assert.ok(bones.hips.getWorldPosition(new THREE.Vector3()).distanceTo(requested) > 1e-4);

    const stableHips = bones.hips.getWorldPosition(new THREE.Vector3());
    const stableFeet = {
        leftFoot: bones.leftFoot.getWorldPosition(new THREE.Vector3()),
        rightFoot: bones.rightFoot.getWorldPosition(new THREE.Vector3())
    };
    controller.drag.pelvisConstraints = [];
    assert.equal(controller.solveHipsTarget(requested, controller.drag), false);
    assert.ok(bones.hips.getWorldPosition(new THREE.Vector3()).distanceTo(stableHips) < 1e-10);
    assert.ok(bones.leftFoot.getWorldPosition(new THREE.Vector3())
        .distanceTo(stableFeet.leftFoot) < 1e-10);
    assert.ok(bones.rightFoot.getWorldPosition(new THREE.Vector3())
        .distanceTo(stableFeet.rightFoot) < 1e-10);
    controller.dispose();
});

test('neck drag distributes spine rotation while preserving hips and explicit hand targets', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const { vrm, bones } = createHumanoidVrm(scene, false);
    controller.setVrm(vrm);
    controller.setPoseMode(true);
    const leftHandTarget = bones.leftHand.getWorldPosition(new THREE.Vector3());
    const rightHandTarget = bones.rightHand.getWorldPosition(new THREE.Vector3());
    controller.explicitEffectorTargets.set('leftHand', leftHandTarget.clone());
    controller.explicitEffectorTargets.set('rightHand', rightHandTarget.clone());
    const hipsPosition = bones.hips.getWorldPosition(new THREE.Vector3());

    const { camera, domElement } = createCameraAndDom();
    camera.position.set(0, 1.5, 5);
    camera.lookAt(0, 1.5, 0);
    camera.updateMatrixWorld(true);
    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);
    const neckPoint = clientPoint(bones.neck.getWorldPosition(new THREE.Vector3()), camera);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...neckPoint,
        pointerId: 41
    }, camera, domElement), true);
    assert.equal(controller.drag.kind, 'neck');
    assert.equal(controller.selectedBone, 'neck');
    assert.equal(controller.drag.spineChain.length, 3);
    controller.moveHandleDrag({
        ...neckPoint,
        clientX: neckPoint.clientX + 15,
        clientY: neckPoint.clientY + 5,
        pointerId: 41
    }, camera, domElement);
    controller.endHandleDrag(41);

    const rotated = [bones.spine, bones.chest].filter((bone) =>
        1 - Math.abs(bone.quaternion.w) > 1e-5);
    assert.equal(rotated.length, 2);
    assert.ok(bones.hips.getWorldPosition(new THREE.Vector3()).distanceTo(hipsPosition) < 1e-8);
    assert.ok(bones.leftHand.getWorldPosition(new THREE.Vector3()).distanceTo(leftHandTarget) < 1e-4);
    assert.ok(bones.rightHand.getWorldPosition(new THREE.Vector3()).distanceTo(rightHandTarget) < 1e-4);
    assert.ok([...controller.bones.values()].every((bone) =>
        bone.quaternion.toArray().every(Number.isFinite)));
    controller.dispose();
});

test('wrist trackball rotates in 3D and arm IK preserves its world orientation without accumulation', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const { vrm, bones } = createHumanoidVrm(scene);
    controller.setVrm(vrm);
    controller.setPoseMode(true);
    const { camera, domElement } = createCameraAndDom();
    camera.position.set(0, 1.5, 5);
    camera.lookAt(0, 1.5, 0);
    camera.updateMatrixWorld(true);
    scene.updateMatrixWorld(true);
    const handPoint = clientPoint(
        bones.leftHand.getWorldPosition(new THREE.Vector3()),
        camera
    );
    assert.equal(controller.beginWristDrag({
        ...handPoint,
        pointerId: 51
    }, camera, domElement, 'leftHand', 'z'), true);
    controller.moveHandleDrag({
        ...handPoint,
        clientX: handPoint.clientX + 18,
        clientY: handPoint.clientY + 14,
        pointerId: 51
    }, camera, domElement);
    controller.endHandleDrag(51);

    const exported = controller.getCurrentPose();
    const rotatedLocal = bones.leftHand.quaternion.clone();
    controller.resetBone('leftHand');
    assert.ok(equivalentRotation(bones.leftHand.quaternion, controller.neutralRotations.get('leftHand')));
    controller.applyPose(exported);
    assert.ok(equivalentRotation(bones.leftHand.quaternion, rotatedLocal, 1e-8));
    const desiredWorld = controller.explicitWristOrientations.get('leftHand').clone();
    assert.ok(desiredWorld.toArray().filter((value) => Math.abs(value) > 1e-4).length >= 3);
    const target = bones.leftHand.getWorldPosition(new THREE.Vector3())
        .add(new THREE.Vector3(0.05, 0.03, 0.02));
    const first = controller.solveLimbTarget('leftHand', target);
    const afterFirst = bones.leftHand.getWorldQuaternion(new THREE.Quaternion());
    const second = controller.solveLimbTarget('leftHand', target);
    const afterSecond = bones.leftHand.getWorldQuaternion(new THREE.Quaternion());
    assert.ok(first && second);
    assert.ok(equivalentRotation(afterFirst, desiredWorld, 1e-8));
    assert.ok(equivalentRotation(afterSecond, desiredWorld, 1e-8));
    assert.ok(equivalentRotation(afterFirst, afterSecond, 1e-10));
    controller.dispose();
});

test('finger controls update actual bones, copy curl and spread, and reset fingers only', () => {
    const scene = new THREE.Scene();
    const messages = [];
    const controller = new PoseController(scene, (message) => messages.push(message));
    const { vrm, bones } = createHumanoidVrm(scene);
    for (const side of ['left', 'right']) {
        let parent = bones[`${side}Hand`];
        for (const [index, suffix] of ['IndexProximal', 'IndexIntermediate', 'IndexDistal'].entries()) {
            const name = `${side}${suffix}`;
            const joint = new THREE.Object3D();
            joint.name = name;
            joint.position.set(side === 'left' ? -0.08 : 0.08, 0.01, index ? 0.005 : 0);
            parent.add(joint);
            parent = joint;
            bones[name] = joint;
            vrm.humanoid.humanBones[name] = { node: joint };
        }
    }
    controller.setVrm(vrm);
    controller.setPoseMode(true);
    const leftHandBefore = bones.leftHand.quaternion.clone();
    assert.equal(controller.beginFingerCurl('left', 'index'), true);
    assert.equal(controller.setFingerCurl('left', 'index', 0.75), true);
    assert.equal(controller.endFingerCurl('left', 'index'), true);
    assert.ok(bones.leftIndexProximal.quaternion.angleTo(new THREE.Quaternion()) > 0.1);
    assert.equal(messages.at(-1).selectedBone, 'leftIndexProximal');
    assert.ok(Math.abs(messages.at(-1).hands.left.fingers.index.value - 0.75) < 1e-6);
    assert.equal(controller.beginFingerSpread('left'), true);
    assert.equal(controller.setFingerSpread('left', 0.6), true);
    assert.equal(controller.endFingerSpread('left'), true);
    assert.ok(Math.abs(messages.at(-1).hands.left.fingerSpread - 0.6) < 1e-6);
    assert.ok(Math.abs(messages.at(-1).hands.left.fingers.index.value - 0.75) < 1e-6);

    assert.equal(controller.copyFingersToOtherHand('left'), true);
    assert.ok(bones.rightIndexProximal.quaternion.angleTo(new THREE.Quaternion()) > 0.1);
    assert.ok(Math.abs(
        messages.at(-1).hands.right.fingers.index.value -
        messages.at(-1).hands.left.fingers.index.value
    ) < 1e-6);
    assert.ok(Math.abs(
        messages.at(-1).hands.right.fingerSpread -
        messages.at(-1).hands.left.fingerSpread
    ) < 1e-6);

    const exported = controller.getCurrentPose();
    const exportedLeft = bones.leftIndexProximal.quaternion.clone();
    const exportedRight = bones.rightIndexProximal.quaternion.clone();
    controller.resetAll();
    assert.ok(equivalentRotation(bones.leftIndexProximal.quaternion, new THREE.Quaternion()));
    controller.applyPose(exported);
    assert.ok(equivalentRotation(bones.leftIndexProximal.quaternion, exportedLeft, 1e-8));
    assert.ok(equivalentRotation(bones.rightIndexProximal.quaternion, exportedRight, 1e-8));

    assert.equal(controller.resetHandFingers('left'), true);
    assert.ok(equivalentRotation(bones.leftIndexProximal.quaternion, new THREE.Quaternion()));
    assert.ok(Math.abs(messages.at(-1).hands.left.fingerSpread) < 1e-9);
    assert.ok(equivalentRotation(bones.leftHand.quaternion, leftHandBefore));
    assert.ok(bones.rightIndexProximal.quaternion.angleTo(new THREE.Quaternion()) > 0.1);

    const fingerlessScene = new THREE.Scene();
    const fingerlessController = new PoseController(fingerlessScene, () => {});
    const fingerless = createHumanoidVrm(fingerlessScene);
    fingerlessController.setVrm(fingerless.vrm);
    fingerlessController.setPoseMode(true);
    const ignored = fingerlessController.applyPose(exported);
    assert.ok(ignored.includes('leftIndexProximal'));
    assert.ok(ignored.includes('rightIndexDistal'));
    fingerlessController.dispose();
    controller.dispose();
});

test('PoseController keeps semantic curl through spread, notifications, and a small re-edit', () => {
    const scene = new THREE.Scene();
    const messages = [];
    const controller = new PoseController(scene, (message) => messages.push(message));
    const { vrm, bones } = createHumanoidVrm(scene);
    addRealisticFingerBones(vrm, bones, new Set(['rightLittleDistal']));
    controller.setVrm(vrm);
    controller.setPoseMode(true);

    // Exercise the real-model case where the two local axes are not perfectly
    // orthogonal instead of relying on the idealized geometric fixture.
    const indexJoints = controller.fingerRig.left.fingers.index;
    indexJoints[0].spreadAxis
        .addScaledVector(indexJoints[0].axis, 0.23)
        .normalize();

    assert.equal(controller.beginFingerCurl('left', 'index'), true);
    assert.equal(controller.setFingerCurl('left', 'index', 1), true);
    assert.equal(controller.endFingerCurl('left', 'index'), true);
    const curled = indexJoints.map((joint) => joint.node.quaternion.clone());

    assert.equal(controller.beginFingerSpread('left'), true);
    assert.equal(controller.setFingerSpread('left', 0.5), true);
    assert.equal(controller.endFingerSpread('left'), true);
    const spreadApplied = indexJoints.map((joint) => joint.node.quaternion.clone());

    assert.ok(!equivalentRotation(spreadApplied[0], curled[0], 1e-8));
    assert.ok(equivalentRotation(spreadApplied[1], curled[1], 1e-10));
    assert.ok(equivalentRotation(spreadApplied[2], curled[2], 1e-10));
    assert.equal(messages.at(-1).hands.left.fingers.index.value, 1);
    assert.equal(messages.at(-1).hands.left.fingerSpread, 0.5);

    const beforeNotifications = spreadApplied.map((rotation) => rotation.clone());
    controller.notifyState('pose-changed');
    controller.notifyState('pose-changed');
    assert.equal(messages.at(-1).hands.left.fingers.index.value, 1);
    assert.equal(messages.at(-1).hands.left.fingerSpread, 0.5);
    indexJoints.forEach((joint, index) => {
        assert.ok(equivalentRotation(joint.node.quaternion, beforeNotifications[index], 1e-10));
    });

    assert.equal(controller.beginFingerCurl('left', 'index'), true);
    assert.equal(controller.setFingerCurl('left', 'index', 0.95), true);
    assert.equal(controller.endFingerCurl('left', 'index'), true);
    const smallEdit = indexJoints.map((joint) => joint.node.quaternion.clone());
    for (const [index, joint] of indexJoints.entries()) {
        const change = spreadApplied[index].angleTo(smallEdit[index]);
        const expected = THREE.MathUtils.degToRad([64, 82, 68][joint.index] * 0.05);
        assert.ok(Math.abs(change - expected) < 1e-6);
    }
    assert.equal(messages.at(-1).hands.left.fingers.index.value, 0.95);
    assert.equal(messages.at(-1).hands.left.fingerSpread, 0.5);

    assert.equal(controller.beginFingerCurl('left', 'index'), true);
    assert.equal(controller.setFingerCurl('left', 'index', 0.95), false);
    assert.equal(controller.endFingerCurl('left', 'index'), true);
    indexJoints.forEach((joint, index) => {
        assert.ok(equivalentRotation(joint.node.quaternion, smallEdit[index], 1e-10));
    });

    controller.dispose();
});

test('PoseController keeps spread through curl and safely rebases manual edit and import', () => {
    const scene = new THREE.Scene();
    const messages = [];
    const controller = new PoseController(scene, (message) => messages.push(message));
    const { vrm, bones } = createHumanoidVrm(scene);
    addRealisticFingerBones(vrm, bones);
    controller.setVrm(vrm);
    controller.setPoseMode(true);

    assert.equal(controller.beginFingerSpread('right'), true);
    assert.equal(controller.setFingerSpread('right', 0.7), true);
    assert.equal(controller.endFingerSpread('right'), true);
    assert.equal(controller.beginFingerCurl('right', 'ring'), true);
    assert.equal(controller.setFingerCurl('right', 'ring', 0.8), true);
    assert.equal(controller.endFingerCurl('right', 'ring'), true);
    assert.equal(messages.at(-1).hands.right.fingerSpread, 0.7);
    assert.equal(messages.at(-1).hands.right.fingers.ring.value, 0.8);

    const intermediate = bones.rightRingIntermediate;
    controller.setBoneEuler('rightRingIntermediate', 11, -7, 19);
    const manualEdit = intermediate.quaternion.clone();
    assert.equal(controller.beginFingerCurl('right', 'ring'), true);
    assert.equal(controller.setFingerCurl('right', 'ring', 0.8), false);
    assert.equal(controller.endFingerCurl('right', 'ring'), true);
    assert.ok(equivalentRotation(intermediate.quaternion, manualEdit, 1e-10));

    const exported = controller.getCurrentPose();
    controller.resetAll();
    controller.applyPose(exported);
    const imported = controller.fingerRig.right.fingers.ring
        .map((joint) => joint.node.quaternion.clone());
    const importedState = messages.at(-1).hands.right;
    assert.equal(controller.beginFingerSpread('right'), true);
    assert.equal(
        controller.setFingerSpread('right', importedState.fingerSpread),
        false
    );
    assert.equal(controller.endFingerSpread('right'), true);
    controller.fingerRig.right.fingers.ring.forEach((joint, index) => {
        assert.ok(equivalentRotation(joint.node.quaternion, imported[index], 1e-9));
    });

    const currentCurl = messages.at(-1).hands.right.fingers.ring.value;
    const proximalBeforeReset = bones.rightRingProximal.quaternion.clone();
    assert.equal(controller.resetBone('rightRingIntermediate'), true);
    assert.ok(equivalentRotation(
        bones.rightRingIntermediate.quaternion,
        controller.neutralRotations.get('rightRingIntermediate')
    ));
    assert.ok(equivalentRotation(
        bones.rightRingProximal.quaternion,
        proximalBeforeReset,
        1e-10
    ));
    assert.equal(controller.beginFingerCurl('right', 'ring'), true);
    assert.equal(controller.setFingerCurl('right', 'ring', currentCurl), false);
    assert.equal(controller.endFingerCurl('right', 'ring'), true);
    assert.ok(equivalentRotation(
        bones.rightRingIntermediate.quaternion,
        controller.neutralRotations.get('rightRingIntermediate')
    ));

    controller.dispose();
});

test('head is selected only by click, shows one exclusive gizmo, and hover does not select it', () => {
    const scene = new THREE.Scene();
    const messages = [];
    const controller = new PoseController(scene, (message) => messages.push(message));
    const { vrm, bones } = createHumanoidVrm(scene);
    controller.setVrm(vrm);
    controller.setPoseMode(true);

    const { camera, domElement } = createCameraAndDom();
    camera.position.set(0, 1.8, 5);
    camera.lookAt(0, 1.8, 0);
    camera.updateMatrixWorld(true);
    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);

    const headGroup = controller.rotationHandles.get('head');
    const headPoint = clientPoint(
        controller.headHitTarget.getWorldPosition(new THREE.Vector3()),
        camera
    );
    assert.equal(headGroup.visible, false);
    controller.updateHover(headPoint, camera, domElement);
    assert.equal(controller.selectedRotationTarget, '');
    assert.equal(headGroup.visible, false);

    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...headPoint,
        pointerId: 71
    }, camera, domElement), true);
    assert.equal(controller.selectedRotationTarget, 'head');
    assert.equal(controller.selectedBone, 'head');
    assert.equal(headGroup.visible, true);
    assert.equal(controller.selectedLimb, '');
    assert.equal(controller.selectedTrunk, '');
    assert.equal(messages.at(-1).selectedBone, 'head');
    controller.endHandleDrag(71);

    assert.equal(controller.beginRotationDrag({
        ...headPoint,
        pointerId: 72
    }, camera, domElement, 'leftFoot', 'z'), true);
    assert.equal(controller.selectedRotationTarget, 'leftFoot');
    assert.equal(controller.selectedBone, 'leftFoot');
    assert.equal(headGroup.visible, false);
    controller.endHandleDrag(72);

    assert.equal(controller.selectHead({ pointerId: 79 }), true);
    controller.endHandleDrag(79);
    const leftHandPoint = clientPoint(
        bones.leftHand.getWorldPosition(new THREE.Vector3()),
        camera
    );
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...leftHandPoint,
        pointerId: 80
    }, camera, domElement), true);
    assert.equal(controller.drag.kind, 'effector');
    assert.equal(controller.selectedRotationTarget, '');
    assert.equal(headGroup.visible, false);
    controller.endHandleDrag(80);

    controller.setSelectedBone('chest');
    assert.equal(controller.selectedRotationTarget, '');
    assert.equal(headGroup.visible, false);
    controller.dispose();
});

test('head rotation changes only head manual rotation and supports undo, redo, reset, and pose round-trip', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const { vrm, bones } = createHumanoidVrm(scene);
    controller.setVrm(vrm);
    controller.setPoseMode(true);
    const { camera, domElement } = createCameraAndDom();
    camera.position.set(0, 1.8, 5);
    camera.lookAt(0, 1.8, 0);
    camera.updateMatrixWorld(true);
    scene.updateMatrixWorld(true);
    const headPoint = clientPoint(
        controller.rotationHandles.get('head').position,
        camera
    );
    const neckBefore = bones.neck.quaternion.clone();
    const chestBefore = bones.chest.quaternion.clone();
    const headNeutral = bones.head.quaternion.clone();

    assert.equal(controller.selectHead({ pointerId: 73 }), true);
    controller.endHandleDrag(73);
    assert.equal(controller.beginRotationDrag({
        ...headPoint,
        pointerId: 74
    }, camera, domElement, 'head', 'x'), true);
    controller.moveHandleDrag({
        ...headPoint,
        clientX: headPoint.clientX + 24,
        clientY: headPoint.clientY + 15,
        pointerId: 74
    }, camera, domElement);
    controller.endHandleDrag(74);
    const rotatedHead = bones.head.quaternion.clone();
    assert.ok(!equivalentRotation(rotatedHead, headNeutral, 1e-8));
    assert.ok(equivalentRotation(bones.neck.quaternion, neckBefore));
    assert.ok(equivalentRotation(bones.chest.quaternion, chestBefore));

    assert.equal(controller.undo(), true);
    assert.ok(equivalentRotation(bones.head.quaternion, headNeutral, 1e-8));
    assert.equal(controller.redo(), true);
    assert.ok(equivalentRotation(bones.head.quaternion, rotatedHead, 1e-8));

    const exported = controller.getCurrentPose();
    assert.equal(controller.resetBone('head'), true);
    assert.ok(equivalentRotation(bones.head.quaternion, headNeutral, 1e-8));
    assert.deepEqual(controller.applyPose(exported), []);
    assert.ok(equivalentRotation(bones.head.quaternion, rotatedHead, 1e-8));
    controller.dispose();
});

test('left and right foot rotation use the shared gizmo and remain stable through repeated leg IK', () => {
    for (const side of ['left', 'right']) {
        const scene = new THREE.Scene();
        const controller = new PoseController(scene, () => {});
        const { vrm, bones } = createHumanoidVrm(scene);
        controller.setVrm(vrm);
        controller.setPoseMode(true);
        const { camera, domElement } = createCameraAndDom();
        camera.position.set(0, 1.4, 5);
        camera.lookAt(0, 1.4, 0);
        camera.updateMatrixWorld(true);
        scene.updateMatrixWorld(true);

        const footName = `${side}Foot`;
        const otherFootName = `${side === 'left' ? 'right' : 'left'}Foot`;
        const footPoint = clientPoint(
            controller.rotationHandles.get(footName).position,
            camera
        );
        const otherBefore = bones[otherFootName].quaternion.clone();
        assert.equal(controller.beginRotationDrag({
            ...footPoint,
            pointerId: side === 'left' ? 75 : 76
        }, camera, domElement, footName, 'y'), true);
        controller.moveHandleDrag({
            ...footPoint,
            clientX: footPoint.clientX + 20,
            clientY: footPoint.clientY - 13,
            pointerId: side === 'left' ? 75 : 76
        }, camera, domElement);
        controller.endHandleDrag(side === 'left' ? 75 : 76);
        assert.equal(controller.selectedRotationTarget, footName);
        assert.equal(controller.selectedBone, footName);
        assert.ok(equivalentRotation(bones[otherFootName].quaternion, otherBefore));

        const rotatedLocal = bones[footName].quaternion.clone();
        const desiredWorld = controller.explicitEndOrientations.get(footName).clone();
        assert.equal(controller.undo(), true);
        assert.ok(equivalentRotation(
            bones[footName].quaternion,
            controller.neutralRotations.get(footName),
            1e-8
        ));
        assert.equal(controller.redo(), true);
        assert.ok(equivalentRotation(bones[footName].quaternion, rotatedLocal, 1e-8));

        const target = bones[footName].getWorldPosition(new THREE.Vector3())
            .add(new THREE.Vector3(side === 'left' ? -0.035 : 0.035, 0.025, 0.02));
        assert.ok(controller.solveLimbTarget(footName, target));
        const firstWorld = bones[footName].getWorldQuaternion(new THREE.Quaternion());
        assert.ok(controller.solveLimbTarget(footName, target));
        const secondWorld = bones[footName].getWorldQuaternion(new THREE.Quaternion());
        assert.ok(equivalentRotation(firstWorld, desiredWorld, 1e-8));
        assert.ok(equivalentRotation(secondWorld, desiredWorld, 1e-8));
        assert.ok(equivalentRotation(firstWorld, secondWorld, 1e-10));

        const exported = controller.getCurrentPose();
        const exportedFoot = bones[footName].quaternion.clone();
        controller.resetAll();
        assert.ok(equivalentRotation(
            bones[footName].quaternion,
            controller.neutralRotations.get(footName),
            1e-8
        ));
        assert.deepEqual(controller.applyPose(exported), []);
        assert.ok(equivalentRotation(bones[footName].quaternion, exportedFoot, 1e-8));
        controller.dispose();
    }
});

test('normalized VRM 0.x and 1.0 equivalent rigs expose head and ankle rotation targets safely', () => {
    for (const includeUpperChest of [false, true]) {
        const scene = new THREE.Scene();
        const controller = new PoseController(scene, () => {});
        const { vrm } = createHumanoidVrm(scene, includeUpperChest);
        controller.setVrm(vrm);
        controller.setPoseMode(true);
        assert.equal(controller.headHitTarget.visible, true);
        assert.equal(controller.rotationHandles.get('head').visible, false);
        for (const boneName of ['leftFoot', 'rightFoot']) {
            assert.equal(controller.rotationHandles.get(boneName).visible, true);
            assert.equal(controller.rotationHitTargets.get(boneName).length, 3);
        }
        assert.equal(controller.selectHead({ pointerId: includeUpperChest ? 78 : 77 }), true);
        assert.equal(controller.rotationHandles.get('head').visible, true);
        controller.endHandleDrag(includeUpperChest ? 78 : 77);
        controller.setVrm(null);
        assert.equal(controller.selectedRotationTarget, '');
        assert.equal(controller.headHitTarget.visible, false);
        controller.dispose();
    }
});

test('spine and chest markers follow normalized bones, update their link, and select exclusively', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const { vrm, bones } = createHumanoidVrm(scene);
    controller.setVrm(vrm);
    controller.setPoseMode(true);
    const { camera, domElement } = createCameraAndDom();
    camera.position.set(0, 1.8, 5);
    camera.lookAt(0, 1.8, 0);
    camera.updateMatrixWorld(true);
    controller.updateHandleScale(camera, 720);
    scene.updateMatrixWorld(true);

    for (const boneName of ['spine', 'chest']) {
        const bonePosition = bones[boneName].getWorldPosition(new THREE.Vector3());
        assert.ok(controller.bodyMarkers.get(boneName).position.distanceTo(bonePosition) < 1e-10);
        assert.ok(
            controller.bodyMarkerHitTargets.get(boneName).position.distanceTo(bonePosition) <
                1e-10
        );
    }
    const linkPositions = controller.bodyMarkerLink.geometry.attributes.position;
    assert.ok(new THREE.Vector3().fromBufferAttribute(linkPositions, 0)
        .distanceTo(bones.spine.getWorldPosition(new THREE.Vector3())) < 1e-6);
    assert.ok(new THREE.Vector3().fromBufferAttribute(linkPositions, 1)
        .distanceTo(bones.chest.getWorldPosition(new THREE.Vector3())) < 1e-6);

    const chestPoint = clientPoint(bones.chest.getWorldPosition(new THREE.Vector3()), camera);
    controller.updateHover(chestPoint, camera, domElement);
    assert.equal(controller.hoveredRotationTarget, 'chest');
    assert.equal(controller.bodyMarkers.get('chest').material.color.getHex(), 0xffff70);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...chestPoint,
        pointerId: 81
    }, camera, domElement), true);
    assert.equal(controller.selectedRotationTarget, 'chest');
    assert.equal(controller.selectedBone, 'chest');
    assert.equal(controller.rotationHandles.get('chest').visible, true);
    assert.equal(controller.rotationHandles.get('spine').visible, false);
    assert.equal(controller.rotationHandles.get('head').visible, false);
    controller.endHandleDrag(81);

    const spinePoint = clientPoint(bones.spine.getWorldPosition(new THREE.Vector3()), camera);
    assert.equal(controller.beginHandleDrag({
        button: 0,
        shiftKey: false,
        ...spinePoint,
        pointerId: 82
    }, camera, domElement), true);
    assert.equal(controller.selectedRotationTarget, 'spine');
    assert.equal(controller.selectedBone, 'spine');
    assert.equal(controller.rotationHandles.get('spine').visible, true);
    assert.equal(controller.rotationHandles.get('chest').visible, false);
    assert.equal(controller.bodyMarkers.get('spine').material.color.getHex(), 0xffff70);
    controller.endHandleDrag(82);
    controller.dispose();
});

test('spine and chest rotate independently with one history item and pose round-trip', () => {
    for (const boneName of ['spine', 'chest']) {
        const scene = new THREE.Scene();
        const controller = new PoseController(scene, () => {});
        const { vrm, bones } = createHumanoidVrm(scene);
        controller.setVrm(vrm);
        controller.setPoseMode(true);
        const { camera, domElement } = createCameraAndDom();
        camera.position.set(0, 1.8, 5);
        camera.lookAt(0, 1.8, 0);
        camera.updateMatrixWorld(true);
        scene.updateMatrixWorld(true);

        const otherBoneName = boneName === 'spine' ? 'chest' : 'spine';
        const targetNeutral = bones[boneName].quaternion.clone();
        const otherBefore = bones[otherBoneName].quaternion.clone();
        const headWorldBefore = bones.head.getWorldQuaternion(new THREE.Quaternion());
        const historyBefore = controller.undoStack.length;
        const center = clientPoint(
            controller.rotationHandles.get(boneName).position,
            camera
        );
        assert.equal(controller.selectRotationTarget({ pointerId: 83 }, boneName), true);
        controller.endHandleDrag(83);
        assert.equal(controller.beginRotationDrag({
            ...center,
            pointerId: 84
        }, camera, domElement, boneName, 'z'), true);
        controller.moveHandleDrag({
            ...center,
            clientX: center.clientX + 22,
            clientY: center.clientY + 14,
            pointerId: 84
        }, camera, domElement);
        controller.endHandleDrag(84);

        const rotated = bones[boneName].quaternion.clone();
        assert.ok(!equivalentRotation(rotated, targetNeutral, 1e-8));
        assert.ok(equivalentRotation(bones[otherBoneName].quaternion, otherBefore, 1e-10));
        assert.ok(!equivalentRotation(
            bones.head.getWorldQuaternion(new THREE.Quaternion()),
            headWorldBefore,
            1e-8
        ));
        assert.equal(controller.undoStack.length, historyBefore + 1);

        assert.equal(controller.undo(), true);
        assert.ok(equivalentRotation(bones[boneName].quaternion, targetNeutral, 1e-8));
        assert.equal(controller.redo(), true);
        assert.ok(equivalentRotation(bones[boneName].quaternion, rotated, 1e-8));

        const exported = controller.getCurrentPose();
        controller.resetAll();
        assert.ok(equivalentRotation(bones[boneName].quaternion, targetNeutral, 1e-8));
        assert.deepEqual(controller.applyPose(exported), []);
        assert.ok(equivalentRotation(bones[boneName].quaternion, rotated, 1e-8));
        assert.equal(controller.resetBone(boneName), true);
        assert.ok(equivalentRotation(bones[boneName].quaternion, targetNeutral, 1e-8));
        controller.dispose();
    }
});

test('head rotation gizmo alone uses a three-times size multiplier without changing drag sensitivity', () => {
    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const { vrm, bones } = createHumanoidVrm(scene);
    controller.setVrm(vrm);
    controller.setPoseMode(true);
    const { camera, domElement } = createCameraAndDom();
    camera.position.set(0, 1.8, 5);
    camera.lookAt(0, 1.8, 0);
    camera.updateMatrixWorld(true);
    scene.updateMatrixWorld(true);

    const expectedBaseScale = (position) => {
        const worldPerPixel = 2 * camera.position.distanceTo(position) *
            Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / 720;
        return THREE.MathUtils.clamp(worldPerPixel * 25 * 1.12, 0.018, 0.28);
    };

    controller.selectRotationTarget({ pointerId: 85 }, 'head');
    controller.endHandleDrag(85);
    controller.updateHandleScale(camera, 720);
    const headGroup = controller.rotationHandles.get('head');
    assert.ok(Math.abs(
        headGroup.scale.x - expectedBaseScale(headGroup.position) * 3
    ) < 1e-10);
    const headVisual = headGroup.children.find((child) => child.material?.colorWrite !== false);
    const headHit = controller.rotationHitTargets.get('head')[0];
    assert.equal(headVisual.geometry.parameters.radius, headHit.geometry.parameters.radius);
    assert.equal(headVisual.parent, headHit.parent);

    controller.selectRotationTarget({ pointerId: 86 }, 'chest');
    controller.endHandleDrag(86);
    controller.updateHandleScale(camera, 720);
    const chestGroup = controller.rotationHandles.get('chest');
    assert.ok(Math.abs(
        chestGroup.scale.x - expectedBaseScale(chestGroup.position)
    ) < 1e-10);
    const leftHandGroup = controller.rotationHandles.get('leftHand');
    const leftHandWorldPerPixel = 2 * camera.position.distanceTo(leftHandGroup.position) *
        Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / 720;
    assert.ok(Math.abs(
        leftHandGroup.scale.x -
        THREE.MathUtils.clamp(leftHandWorldPerPixel * 25, 0.018, 0.28)
    ) < 1e-10);

    const rotateBySamePointerDelta = (target, pointerId) => {
        const point = clientPoint(controller.rotationHandles.get(target).position, camera);
        assert.equal(controller.beginRotationDrag({
            ...point,
            pointerId
        }, camera, domElement, target, 'x'), true);
        controller.moveHandleDrag({
            ...point,
            clientX: point.clientX + 18,
            clientY: point.clientY + 11,
            pointerId
        }, camera, domElement);
        controller.endHandleDrag(pointerId);
        return bones[target].quaternion.angleTo(controller.neutralRotations.get(target));
    };
    const chestAngle = rotateBySamePointerDelta('chest', 87);
    controller.resetBone('chest');
    const headAngle = rotateBySamePointerDelta('head', 88);
    assert.ok(Math.abs(headAngle - chestAngle) < 1e-10);
    controller.dispose();
});

test('body rotation markers handle missing bones and normalized VRM 0.x and 1.0 equivalents', () => {
    for (const includeUpperChest of [false, true]) {
        const scene = new THREE.Scene();
        const controller = new PoseController(scene, () => {});
        const { vrm } = createHumanoidVrm(scene, includeUpperChest);
        controller.setVrm(vrm);
        controller.setPoseMode(true);
        const { camera, domElement } = createCameraAndDom();
        camera.position.set(0, 1.8, 5);
        camera.lookAt(0, 1.8, 0);
        camera.updateMatrixWorld(true);
        scene.updateMatrixWorld(true);
        for (const boneName of ['spine', 'chest']) {
            assert.equal(controller.bodyMarkers.get(boneName).visible, true);
            assert.equal(controller.selectRotationTarget({ pointerId: 89 }, boneName), true);
            controller.endHandleDrag(89);
            assert.equal(controller.rotationHandles.get(boneName).visible, true);
            const neutral = controller.neutralRotations.get(boneName);
            const point = clientPoint(controller.rotationHandles.get(boneName).position, camera);
            assert.equal(controller.beginRotationDrag({
                ...point,
                pointerId: 91
            }, camera, domElement, boneName, 'y'), true);
            controller.moveHandleDrag({
                ...point,
                clientX: point.clientX + 12,
                clientY: point.clientY + 7,
                pointerId: 91
            }, camera, domElement);
            controller.endHandleDrag(91);
            assert.ok(!equivalentRotation(controller.bones.get(boneName).quaternion, neutral, 1e-8));
            assert.ok(controller.bones.get(boneName).quaternion.toArray().every(Number.isFinite));
            controller.resetBone(boneName);
        }
        controller.dispose();
    }

    const scene = new THREE.Scene();
    const controller = new PoseController(scene, () => {});
    const { vrm } = createHumanoidVrm(scene);
    delete vrm.humanoid.humanBones.chest;
    controller.setVrm(vrm);
    controller.setPoseMode(true);
    assert.equal(controller.bodyMarkers.get('spine').visible, true);
    assert.equal(controller.bodyMarkers.get('chest').visible, false);
    assert.equal(controller.bodyMarkerLink.visible, false);
    assert.equal(controller.selectRotationTarget({ pointerId: 90 }, 'chest'), false);
    controller.dispose();
});
