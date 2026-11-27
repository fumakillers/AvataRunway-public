import * as THREE from 'three';

const FRAME_FILL_RATIO = 0.8;
const MAX_PITCH = Math.PI / 2;
const PITCH_EPSILON = 0.001;
const ORBIT_CONTROLS_ZOOM_SPEED = 1.0;

function clamp(value, minimum, maximum) {
    return Math.min(Math.max(value, minimum), maximum);
}

function normalizeAngle(angle) {
    const fullTurn = Math.PI * 2;
    return ((angle + Math.PI) % fullTurn + fullTurn) % fullTurn - Math.PI;
}

export function isCameraResetShortcut(event) {
    return event.code === 'KeyR' &&
        !event.ctrlKey &&
        !event.altKey &&
        !event.metaKey &&
        !event.repeat;
}

export function calculateOrbitControlsZoomScale(deltaY) {
    return Math.pow(0.95, ORBIT_CONTROLS_ZOOM_SPEED * Math.abs(deltaY * 0.01));
}

function getRawBoneNode(vrm, boneName) {
    const humanoid = vrm?.humanoid;
    return humanoid?.getRawBoneNode?.(boneName) ??
        humanoid?.rawHumanBones?.[boneName]?.node ??
        null;
}

function getBoneWorldPosition(vrm, boneName) {
    const node = getRawBoneNode(vrm, boneName);
    if (!node) {
        return null;
    }

    node.updateWorldMatrix(true, false);
    return node.getWorldPosition(new THREE.Vector3());
}

export function calculateVrmBodyTarget(vrm, box) {
    const fallbackCenter = box.getCenter(new THREE.Vector3());
    const torsoPositions = new Map(
        ['hips', 'spine', 'chest', 'upperChest']
            .map((boneName) => [boneName, getBoneWorldPosition(vrm, boneName)])
    );
    const horizontalAnchor = ['hips', 'spine', 'chest', 'upperChest']
        .map((boneName) => torsoPositions.get(boneName))
        .find(Boolean);

    if (!horizontalAnchor) {
        return fallbackCenter;
    }

    const head = getBoneWorldPosition(vrm, 'head');
    const feet = ['leftFoot', 'rightFoot', 'leftToes', 'rightToes']
        .map((boneName) => getBoneWorldPosition(vrm, boneName))
        .filter(Boolean);
    let verticalCenter = fallbackCenter.y;

    if (head && feet.length > 0) {
        const torsoBelowHead = ['upperChest', 'chest', 'spine', 'hips']
            .map((boneName) => torsoPositions.get(boneName))
            .find((position) => position && position.y < head.y);
        const headTopAllowance = torsoBelowHead
            ? Math.max(head.y - torsoBelowHead.y, 0) * 0.5
            : 0;
        const estimatedHeadTop = Math.min(head.y + headTopAllowance, box.max.y);
        const feetY = feet.reduce((sum, position) => sum + position.y, 0) / feet.length;
        verticalCenter = clamp((estimatedHeadTop + feetY) / 2, box.min.y, box.max.y);
    }

    return new THREE.Vector3(
        horizontalAnchor.x,
        verticalCenter,
        horizontalAnchor.z
    );
}

export class VrmCameraController {
    constructor(perspectiveCamera, orthographicCamera) {
        this.perspectiveCamera = perspectiveCamera;
        this.orthographicCamera = orthographicCamera;
        this.projection = 'perspective';
        this.baseTarget = new THREE.Vector3();
        this.target = new THREE.Vector3();
        this.panOffset = new THREE.Vector3();
        this.yaw = 0;
        this.pitch = 0;
        this.distance = 3;
        this.orthographicZoom = 1;
        this.minDistance = 0;
        this.maxDistance = Infinity;
        this.minOrthographicZoom = 0;
        this.maxOrthographicZoom = Infinity;
        this.modelRadius = 1;
        this.initialBox = null;
        this.initialState = null;
    }

    get camera() {
        return this.projection === 'orthographic'
            ? this.orthographicCamera
            : this.perspectiveCamera;
    }

    get visibleHalfHeight() {
        if (this.projection === 'orthographic') {
            return 1 / this.orthographicZoom;
        }

        const halfFov = THREE.MathUtils.degToRad(this.perspectiveCamera.fov) / 2;
        return this.distance * Math.tan(halfFov);
    }

    resize(aspect) {
        const safeAspect = Math.max(aspect, Number.EPSILON);
        this.perspectiveCamera.aspect = safeAspect;
        this.perspectiveCamera.updateProjectionMatrix();

        this.orthographicCamera.left = -safeAspect;
        this.orthographicCamera.right = safeAspect;
        this.orthographicCamera.top = 1;
        this.orthographicCamera.bottom = -1;
        this.orthographicCamera.updateProjectionMatrix();
        this.apply();
    }

    fitToBox(box, baseTarget = null) {
        const center = baseTarget?.clone() ?? box.getCenter(new THREE.Vector3());
        const framing = this.calculateFraming(box, center);

        this.initialBox = box.clone();
        this.initialState = {
            baseTarget: center.clone(),
            panOffset: new THREE.Vector3(),
            yaw: 0,
            pitch: 0,
            distance: framing.distance,
            projection: 'perspective',
            orthographicZoom: framing.orthographicZoom,
            cameraUp: new THREE.Vector3(0, 1, 0)
        };
        this.restoreInitialState(framing);
    }

    calculateFraming(box, center) {
        const sphere = box.getBoundingSphere(new THREE.Sphere());
        const verticalHalfFov = THREE.MathUtils.degToRad(this.perspectiveCamera.fov) / 2;
        const horizontalHalfFov = Math.atan(
            Math.tan(verticalHalfFov) * this.perspectiveCamera.aspect
        );
        const verticalRadius = Math.max(box.max.y - center.y, center.y - box.min.y);
        const horizontalRadius = Math.max(box.max.x - center.x, center.x - box.min.x);
        const depthRadius = Math.max(box.max.z - center.z, center.z - box.min.z);
        const distanceForHeight = verticalRadius /
            (FRAME_FILL_RATIO * Math.tan(verticalHalfFov));
        const distanceForWidth = horizontalRadius /
            (FRAME_FILL_RATIO * Math.tan(horizontalHalfFov));
        const depthPadding = depthRadius;
        const fittedDistance = depthPadding + Math.max(distanceForHeight, distanceForWidth, 0.1);
        const modelRadius = Math.max(sphere.radius, 0.1);
        const perspectiveHalfHeight = fittedDistance * Math.tan(verticalHalfFov);
        return {
            distance: fittedDistance,
            orthographicZoom: 1 / perspectiveHalfHeight,
            modelRadius
        };
    }

    restoreInitialState(framing) {
        const initial = this.initialState;
        if (!initial) {
            return false;
        }

        this.baseTarget.copy(initial.baseTarget);
        this.panOffset.copy(initial.panOffset);
        this.yaw = initial.yaw;
        this.pitch = initial.pitch;
        this.distance = framing.distance;
        this.projection = initial.projection;
        this.orthographicZoom = framing.orthographicZoom;
        this.modelRadius = framing.modelRadius;
        this.perspectiveCamera.up.copy(initial.cameraUp);
        this.orthographicCamera.up.copy(initial.cameraUp);
        this.apply();
        return true;
    }

    resetToInitialView() {
        if (!this.initialBox || !this.initialState) {
            return false;
        }

        const framing = this.calculateFraming(this.initialBox, this.initialState.baseTarget);
        return this.restoreInitialState(framing);
    }

    orbit(deltaYaw, deltaPitch) {
        this.yaw = normalizeAngle(this.yaw + deltaYaw);
        this.pitch = clamp(
            this.pitch + deltaPitch,
            -MAX_PITCH + PITCH_EPSILON,
            MAX_PITCH - PITCH_EPSILON
        );
        this.apply();
    }

    panPixels(deltaX, deltaY, viewportHeight) {
        const worldUnitsPerPixel = (this.visibleHalfHeight * 2) /
            Math.max(viewportHeight, 1);
        const right = new THREE.Vector3().setFromMatrixColumn(this.camera.matrixWorld, 0);
        const up = new THREE.Vector3().setFromMatrixColumn(this.camera.matrixWorld, 1);

        const movement = right.multiplyScalar(-deltaX * worldUnitsPerPixel)
            .add(up.multiplyScalar(deltaY * worldUnitsPerPixel));
        this.panOffset.add(movement);
        this.updateTarget();
        this.apply();
    }

    zoom(deltaY) {
        if (!Number.isFinite(deltaY) || deltaY === 0) {
            return;
        }

        const zoomScale = calculateOrbitControlsZoomScale(deltaY);
        if (this.projection === 'orthographic') {
            this.orthographicZoom = deltaY < 0
                ? this.orthographicZoom / zoomScale
                : this.orthographicZoom * zoomScale;
        } else {
            this.distance = deltaY < 0
                ? this.distance * zoomScale
                : this.distance / zoomScale;
        }

        this.apply();
    }

    toggleProjection() {
        const verticalHalfFov = THREE.MathUtils.degToRad(this.perspectiveCamera.fov) / 2;
        if (this.projection === 'perspective') {
            this.orthographicZoom = 1 / (this.distance * Math.tan(verticalHalfFov));
            this.projection = 'orthographic';
        } else {
            this.distance = (1 / this.orthographicZoom) / Math.tan(verticalHalfFov);
            this.projection = 'perspective';
        }

        this.apply();
    }

    viewFront() {
        this.yaw = 0;
        this.pitch = 0;
        this.apply();
    }

    viewRightFront() {
        this.yaw = Math.PI / 4;
        this.apply();
    }

    viewTop() {
        this.pitch = MAX_PITCH;
        this.apply();
    }

    flip() {
        this.yaw = normalizeAngle(this.yaw + Math.PI);
        this.apply();
    }

    updateTarget() {
        this.target.copy(this.baseTarget).add(this.panOffset);
    }

    apply() {
        this.updateTarget();
        const cosPitch = Math.cos(this.pitch);
        const offset = new THREE.Vector3(
            Math.sin(this.yaw) * cosPitch,
            Math.sin(this.pitch),
            Math.cos(this.yaw) * cosPitch
        ).multiplyScalar(this.distance);
        const activeCamera = this.camera;

        activeCamera.position.copy(this.target).add(offset);
        if (Math.abs(cosPitch) < PITCH_EPSILON) {
            const horizontalDirection = new THREE.Vector3(
                Math.sin(this.yaw),
                0,
                Math.cos(this.yaw)
            );
            activeCamera.up.copy(horizontalDirection).multiplyScalar(this.pitch > 0 ? -1 : 1);
        } else {
            activeCamera.up.set(0, 1, 0);
        }
        activeCamera.lookAt(this.target);

        const far = Math.max(this.distance + this.modelRadius * 4, 100);
        activeCamera.near = Math.max(Math.min(this.distance / 1000, 0.01), 0.001);
        activeCamera.far = far;
        if (activeCamera.isOrthographicCamera) {
            activeCamera.zoom = this.orthographicZoom;
        }
        activeCamera.updateProjectionMatrix();
        activeCamera.updateMatrixWorld(true);
    }

    snapshot() {
        return {
            baseTarget: this.baseTarget.toArray(),
            target: this.target.toArray(),
            panOffset: this.panOffset.toArray(),
            yaw: this.yaw,
            pitch: this.pitch,
            distance: this.distance,
            projection: this.projection,
            orthographicZoom: this.orthographicZoom,
            visibleHalfHeight: this.visibleHalfHeight
        };
    }
}

export { FRAME_FILL_RATIO, ORBIT_CONTROLS_ZOOM_SPEED };
