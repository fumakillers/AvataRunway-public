import * as THREE from 'three';
import {
    FINGER_NAMES,
    HAND_SIDES,
    applyFingerCurlState,
    applyFingerSpreadState,
    buildFingerCurlRig,
    createFingerPoseStates,
    handPoseState,
    rebaseFingerJoint,
    resetFingerPoseState,
    synchronizeFingerPoseState
} from './hand-pose.js';
import { applyTwoBoneIk, stablePerpendicular } from './two-bone-ik.js';
import { constrainPelvisPosition, solveSpineCcd } from './trunk-ik.js';

const EULER_ORDER = 'XYZ';
const HANDLE_COLOR = 0x1689ff;
const HANDLE_RADIUS_PIXELS = 11;
const HANDLE_HIT_RADIUS_PIXELS = 18;
const HANDLE_SEGMENTS = 20;
const HANDLE_RENDER_ORDER = 100000;
const BEND_HANDLE_COLOR = 0xffd84d;
const BEND_HANDLE_HALF_WIDTH_PIXELS = 24;
const BEND_HANDLE_HIT_RADIUS_PIXELS = 27;
const BEND_HANDLE_RENDER_ORDER = HANDLE_RENDER_ORDER + 100;
const TRUNK_HANDLE_RADIUS_PIXELS = 13;
const TRUNK_HANDLE_HIT_RADIUS_PIXELS = 21;
const TRUNK_HANDLE_RENDER_ORDER = BEND_HANDLE_RENDER_ORDER + 100;
const ROTATION_HANDLE_COLOR = 0xffb020;
const ROTATION_HANDLE_ACTIVE_COLOR = 0xffff70;
const ROTATION_HANDLE_RADIUS_PIXELS = 25;
const ROTATION_HANDLE_RENDER_ORDER = TRUNK_HANDLE_RENDER_ORDER + 100;
const ROTATION_TRACKBALL_RADIUS_PIXELS = 34;
const HEAD_HIT_RENDER_ORDER = ROTATION_HANDLE_RENDER_ORDER + 100;
const BODY_MARKER_COLOR = 0xb8c2cc;
const BODY_MARKER_ACTIVE_COLOR = 0xffff70;
const BODY_MARKER_RADIUS_PIXELS = 6;
const BODY_MARKER_HIT_RADIUS_PIXELS = 13;
const BODY_MARKER_RENDER_ORDER = HEAD_HIT_RENDER_ORDER + 100;
const ROTATION_TARGETS = Object.freeze({
    leftHand: { preserveAfterIk: true, side: 'left' },
    rightHand: { preserveAfterIk: true, side: 'right' },
    leftFoot: { preserveAfterIk: true, side: 'left' },
    rightFoot: { preserveAfterIk: true, side: 'right' },
    head: { preserveAfterIk: false, selectionOnly: true, sizeMultiplier: 3 },
    spine: { preserveAfterIk: false, selectionOnly: true },
    chest: { preserveAfterIk: false, selectionOnly: true }
});
const BODY_ROTATION_BONES = Object.freeze(['spine', 'chest']);
const TRUNK_HANDLES = Object.freeze({
    hips: { bone: 'hips', color: 0xff6b35, geometry: 'octahedron' },
    neck: { bone: 'neck', color: 0xcf5cff, geometry: 'torus' }
});
const SPINE_BONES = Object.freeze(['spine', 'chest', 'upperChest', 'neck']);
const LIMBS = Object.freeze({
    leftHand: {
        root: 'leftUpperArm',
        joint: 'leftLowerArm',
        end: 'leftHand',
        fallbackBend: new THREE.Vector3(0, 0, -1)
    },
    rightHand: {
        root: 'rightUpperArm',
        joint: 'rightLowerArm',
        end: 'rightHand',
        fallbackBend: new THREE.Vector3(0, 0, -1)
    },
    leftFoot: {
        root: 'leftUpperLeg',
        joint: 'leftLowerLeg',
        end: 'leftFoot',
        fallbackBend: new THREE.Vector3(0, 0, 1)
    },
    rightFoot: {
        root: 'rightUpperLeg',
        joint: 'rightLowerLeg',
        end: 'rightFoot',
        fallbackBend: new THREE.Vector3(0, 0, 1)
    }
});

function isFiniteQuaternion(quaternion) {
    return quaternion instanceof THREE.Quaternion &&
        quaternion.toArray().every(Number.isFinite) &&
        quaternion.lengthSq() > 1e-12;
}

function normalizeQuaternionValue(value) {
    const quaternion = new THREE.Quaternion(
        Number(value?.x),
        Number(value?.y),
        Number(value?.z),
        Number(value?.w)
    );
    if (!isFiniteQuaternion(quaternion)) {
        throw new TypeError('Pose rotation must be a finite, non-zero quaternion.');
    }
    return quaternion.normalize();
}

function neutralToDelta(neutral, current) {
    if (!isFiniteQuaternion(neutral) || !isFiniteQuaternion(current)) {
        throw new TypeError('Neutral and current rotations must be finite quaternions.');
    }
    return neutral.clone().invert().multiply(current).normalize();
}

function deltaToLocal(neutral, delta) {
    if (!isFiniteQuaternion(neutral) || !isFiniteQuaternion(delta)) {
        throw new TypeError('Neutral and delta rotations must be finite quaternions.');
    }
    return neutral.clone().multiply(delta).normalize();
}

function quaternionToEulerDegrees(quaternion) {
    const normalized = normalizeQuaternionValue(quaternion);
    const euler = new THREE.Euler().setFromQuaternion(normalized, EULER_ORDER);
    return {
        x: THREE.MathUtils.radToDeg(euler.x),
        y: THREE.MathUtils.radToDeg(euler.y),
        z: THREE.MathUtils.radToDeg(euler.z)
    };
}

function eulerDegreesToQuaternion(x, y, z) {
    const values = [Number(x), Number(y), Number(z)];
    if (!values.every(Number.isFinite)) {
        throw new TypeError('Euler angles must be finite numbers.');
    }
    return new THREE.Quaternion()
        .setFromEuler(new THREE.Euler(
            THREE.MathUtils.degToRad(values[0]),
            THREE.MathUtils.degToRad(values[1]),
            THREE.MathUtils.degToRad(values[2]),
            EULER_ORDER
        ))
        .normalize();
}

class PoseController {
    constructor(scene, notify) {
        this.scene = scene;
        this.notify = notify;
        this.vrm = null;
        this.bones = new Map();
        this.neutralRotations = new Map();
        this.neutralHipsPosition = new THREE.Vector3();
        this.avatarHeight = 1;
        this.handleGroup = new THREE.Group();
        this.handleGroup.name = 'AvataRunwayPoseHandles';
        this.handles = new Map();
        this.handleHitTargets = new Map();
        this.explicitBendDirections = new Map();
        this.selectedLimb = '';
        this.bendHandle = null;
        this.bendHitTarget = null;
        this.trunkHandles = new Map();
        this.trunkHitTargets = new Map();
        this.selectedTrunk = '';
        this.hoveredTrunk = '';
        this.explicitEffectorTargets = new Map();
        this.explicitEndOrientations = new Map();
        // Compatibility alias for callers from the original hand-only implementation.
        this.explicitWristOrientations = this.explicitEndOrientations;
        this.rotationHandles = new Map();
        this.rotationHitTargets = new Map();
        this.selectedRotationTarget = '';
        this.hoveredRotationTarget = '';
        this.headHitTarget = null;
        this.bodyMarkers = new Map();
        this.bodyMarkerHitTargets = new Map();
        this.bodyMarkerLink = null;
        this.fingerRig = null;
        this.fingerPoseStates = null;
        this.selectedHand = 'left';
        this.fingerCurlSessions = new Map();
        this.fingerSpreadSessions = new Map();
        this.poseMode = false;
        this.modelLoading = false;
        this.selectedBone = '';
        this.drag = null;
        this.notificationFrame = 0;
        this.undoStack = [];
        this.redoStack = [];
        this.historyTransaction = null;
        this.historyLimit = 100;
        this.raycaster = new THREE.Raycaster();
        this.dragPlane = new THREE.Plane();
        this.scene.add(this.handleGroup);
        this.handleGroup.visible = false;
        this.createHandles();
        this.createBendHandle();
        this.createTrunkHandles();
        this.createRotationHandles();
        // Keep the original hand-only collection names available to existing callers/tests.
        this.wristHandles = new Map(
            ['leftHand', 'rightHand'].map((name) => [name, this.rotationHandles.get(name)])
        );
        this.wristHitTargets = new Map(
            ['leftHand', 'rightHand'].map((name) => [name, this.rotationHitTargets.get(name)])
        );
        this.createHeadHitTarget();
        this.createBodyMarkers();
    }

    createHandles() {
        const geometry = new THREE.SphereGeometry(1, HANDLE_SEGMENTS, Math.floor(HANDLE_SEGMENTS * 0.75));
        const handleMaterial = new THREE.MeshBasicMaterial({
            color: HANDLE_COLOR,
            transparent: true,
            opacity: 1,
            depthTest: false,
            depthWrite: false,
            toneMapped: false
        });
        const hitMaterial = new THREE.MeshBasicMaterial({
            color: 0x000000,
            transparent: true,
            opacity: 0,
            depthTest: false,
            depthWrite: false,
            colorWrite: false,
            toneMapped: false
        });
        for (const endName of Object.keys(LIMBS)) {
            const handle = new THREE.Mesh(geometry, handleMaterial);
            handle.name = `PoseHandle:${endName}`;
            handle.userData.poseEndBone = endName;
            handle.renderOrder = HANDLE_RENDER_ORDER;

            const hitTarget = new THREE.Mesh(geometry, hitMaterial);
            hitTarget.name = `PoseHandleHitTarget:${endName}`;
            hitTarget.userData.poseEndBone = endName;
            hitTarget.renderOrder = HANDLE_RENDER_ORDER + 1;

            this.handleGroup.add(handle, hitTarget);
            this.handles.set(endName, handle);
            this.handleHitTargets.set(endName, hitTarget);
        }
    }

    createBendHandle() {
        const shape = new THREE.Shape();
        shape.moveTo(-1, 0);
        shape.lineTo(-0.55, 0.45);
        shape.lineTo(-0.55, 0.18);
        shape.lineTo(0.55, 0.18);
        shape.lineTo(0.55, 0.45);
        shape.lineTo(1, 0);
        shape.lineTo(0.55, -0.45);
        shape.lineTo(0.55, -0.18);
        shape.lineTo(-0.55, -0.18);
        shape.lineTo(-0.55, -0.45);
        shape.closePath();

        this.bendHandle = new THREE.Mesh(
            new THREE.ShapeGeometry(shape),
            new THREE.MeshBasicMaterial({
                color: BEND_HANDLE_COLOR,
                transparent: true,
                opacity: 1,
                depthTest: false,
                depthWrite: false,
                toneMapped: false,
                side: THREE.DoubleSide
            })
        );
        this.bendHandle.name = 'PoseBendHandle';
        this.bendHandle.renderOrder = BEND_HANDLE_RENDER_ORDER;
        this.bendHandle.visible = false;

        this.bendHitTarget = new THREE.Mesh(
            new THREE.SphereGeometry(1, HANDLE_SEGMENTS, Math.floor(HANDLE_SEGMENTS * 0.75)),
            new THREE.MeshBasicMaterial({
                color: 0x000000,
                transparent: true,
                opacity: 0,
                depthTest: false,
                depthWrite: false,
                colorWrite: false,
                toneMapped: false
            })
        );
        this.bendHitTarget.name = 'PoseBendHandleHitTarget';
        this.bendHitTarget.renderOrder = BEND_HANDLE_RENDER_ORDER + 1;
        this.bendHitTarget.visible = false;
        this.handleGroup.add(this.bendHandle, this.bendHitTarget);
    }

    createTrunkHandles() {
        const hitGeometry = new THREE.SphereGeometry(
            1,
            HANDLE_SEGMENTS,
            Math.floor(HANDLE_SEGMENTS * 0.75)
        );
        const hitMaterial = new THREE.MeshBasicMaterial({
            color: 0x000000,
            transparent: true,
            opacity: 0,
            depthTest: false,
            depthWrite: false,
            colorWrite: false,
            toneMapped: false
        });
        for (const [name, definition] of Object.entries(TRUNK_HANDLES)) {
            const geometry = definition.geometry === 'torus'
                ? new THREE.TorusGeometry(0.72, 0.28, 12, 28)
                : new THREE.OctahedronGeometry(1, 0);
            const handle = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({
                color: definition.color,
                transparent: true,
                opacity: 0.95,
                depthTest: false,
                depthWrite: false,
                toneMapped: false,
                side: THREE.DoubleSide
            }));
            handle.name = `PoseTrunkHandle:${name}`;
            handle.userData.poseTrunkHandle = name;
            handle.renderOrder = TRUNK_HANDLE_RENDER_ORDER;

            const hitTarget = new THREE.Mesh(hitGeometry, hitMaterial);
            hitTarget.name = `PoseTrunkHandleHitTarget:${name}`;
            hitTarget.userData.poseTrunkHandle = name;
            hitTarget.renderOrder = TRUNK_HANDLE_RENDER_ORDER + 1;
            this.handleGroup.add(handle, hitTarget);
            this.trunkHandles.set(name, handle);
            this.trunkHitTargets.set(name, hitTarget);
        }
    }

    createRotationHandles() {
        const axes = [
            { name: 'x', rotation: new THREE.Euler(0, Math.PI / 2, 0) },
            { name: 'y', rotation: new THREE.Euler(Math.PI / 2, 0, 0) },
            { name: 'z', rotation: new THREE.Euler(0, 0, 0) }
        ];
        for (const boneName of Object.keys(ROTATION_TARGETS)) {
            const group = new THREE.Group();
            group.name = `PoseRotationHandle:${boneName}`;
            group.scale.setScalar(0.03);
            const hitTargets = [];
            for (const axis of axes) {
                const ring = new THREE.Mesh(
                    new THREE.TorusGeometry(1.45, 0.055, 8, 48),
                    new THREE.MeshBasicMaterial({
                        color: ROTATION_HANDLE_COLOR,
                        transparent: true,
                        opacity: 0.88,
                        depthTest: false,
                        depthWrite: false,
                        toneMapped: false
                    })
                );
                ring.rotation.copy(axis.rotation);
                ring.renderOrder = ROTATION_HANDLE_RENDER_ORDER;
                ring.userData.poseRotationTarget = boneName;
                ring.userData.poseRotationAxis = axis.name;
                const hit = new THREE.Mesh(
                    new THREE.TorusGeometry(1.45, 0.18, 8, 32),
                    new THREE.MeshBasicMaterial({
                        transparent: true,
                        opacity: 0,
                        depthTest: false,
                        depthWrite: false,
                        colorWrite: false,
                        toneMapped: false
                    })
                );
                hit.rotation.copy(axis.rotation);
                hit.renderOrder = ROTATION_HANDLE_RENDER_ORDER + 1;
                hit.userData.poseRotationTarget = boneName;
                hit.userData.poseRotationAxis = axis.name;
                group.add(ring, hit);
                hitTargets.push(hit);
            }
            this.handleGroup.add(group);
            this.rotationHandles.set(boneName, group);
            this.rotationHitTargets.set(boneName, hitTargets);
        }
    }

    createHeadHitTarget() {
        this.headHitTarget = new THREE.Mesh(
            new THREE.SphereGeometry(1, HANDLE_SEGMENTS, Math.floor(HANDLE_SEGMENTS * 0.75)),
            new THREE.MeshBasicMaterial({
                transparent: true,
                opacity: 0,
                depthTest: false,
                depthWrite: false,
                colorWrite: false,
                toneMapped: false
            })
        );
        this.headHitTarget.name = 'PoseHeadHitTarget';
        this.headHitTarget.userData.poseSelectBone = 'head';
        this.headHitTarget.renderOrder = HEAD_HIT_RENDER_ORDER;
        this.headHitTarget.visible = false;
        this.handleGroup.add(this.headHitTarget);
    }

    createBodyMarkers() {
        const markerGeometry = new THREE.SphereGeometry(
            1,
            HANDLE_SEGMENTS,
            Math.floor(HANDLE_SEGMENTS * 0.75)
        );
        const hitGeometry = markerGeometry.clone();
        for (const boneName of BODY_ROTATION_BONES) {
            const marker = new THREE.Mesh(
                markerGeometry,
                new THREE.MeshBasicMaterial({
                    color: BODY_MARKER_COLOR,
                    transparent: true,
                    opacity: 0.62,
                    depthTest: false,
                    depthWrite: false,
                    toneMapped: false
                })
            );
            marker.name = `PoseBodyMarker:${boneName}`;
            marker.userData.poseBodyBone = boneName;
            marker.renderOrder = BODY_MARKER_RENDER_ORDER;
            marker.scale.setScalar(0.02);

            const hitTarget = new THREE.Mesh(
                hitGeometry,
                new THREE.MeshBasicMaterial({
                    transparent: true,
                    opacity: 0,
                    depthTest: false,
                    depthWrite: false,
                    colorWrite: false,
                    toneMapped: false
                })
            );
            hitTarget.name = `PoseBodyMarkerHitTarget:${boneName}`;
            hitTarget.userData.poseBodyBone = boneName;
            hitTarget.renderOrder = BODY_MARKER_RENDER_ORDER + 1;
            hitTarget.scale.setScalar(0.035);

            this.handleGroup.add(marker, hitTarget);
            this.bodyMarkers.set(boneName, marker);
            this.bodyMarkerHitTargets.set(boneName, hitTarget);
        }

        this.bodyMarkerLink = new THREE.Line(
            new THREE.BufferGeometry().setFromPoints([
                new THREE.Vector3(),
                new THREE.Vector3()
            ]),
            new THREE.LineBasicMaterial({
                color: BODY_MARKER_COLOR,
                transparent: true,
                opacity: 0.34,
                depthTest: false,
                depthWrite: false,
                toneMapped: false
            })
        );
        this.bodyMarkerLink.name = 'PoseBodyMarkerLink';
        this.bodyMarkerLink.renderOrder = BODY_MARKER_RENDER_ORDER - 1;
        this.bodyMarkerLink.visible = false;
        this.handleGroup.add(this.bodyMarkerLink);
    }

    setVrm(vrm) {
        this.cancelDrag();
        this.vrm = vrm ?? null;
        this.bones.clear();
        this.neutralRotations.clear();
        this.explicitBendDirections.clear();
        this.explicitEffectorTargets.clear();
        this.explicitEndOrientations.clear();
        this.fingerCurlSessions.clear();
        this.fingerSpreadSessions.clear();
        this.fingerRig = null;
        this.fingerPoseStates = null;
        this.selectedLimb = '';
        this.selectedTrunk = '';
        this.hoveredTrunk = '';
        this.selectedRotationTarget = '';
        this.hoveredRotationTarget = '';
        this.selectedBone = '';
        this.undoStack.length = 0;
        this.redoStack.length = 0;
        this.historyTransaction = null;
        if (vrm?.humanoid) {
            for (const name of Object.keys(vrm.humanoid.humanBones ?? {})) {
                const node = vrm.humanoid.getNormalizedBoneNode(name);
                if (node) {
                    this.bones.set(name, node);
                    this.neutralRotations.set(name, node.quaternion.clone().normalize());
                }
            }
            this.selectedBone = this.bones.has('hips')
                ? 'hips'
                : this.bones.keys().next().value ?? '';
            const hips = this.bones.get('hips');
            if (hips) {
                this.neutralHipsPosition.copy(hips.position);
            }
            this.avatarHeight = this.computeAvatarHeight();
            vrm.scene.updateWorldMatrix(true, true);
            this.fingerRig = buildFingerCurlRig(this.bones, this.neutralRotations);
            this.fingerPoseStates = createFingerPoseStates(this.fingerRig);
            this.selectedHand = this.fingerRig.left.available
                ? 'left'
                : this.fingerRig.right.available ? 'right' : 'left';
            this.rebuildEndOrientationsFromCurrentPose();
        }
        this.updateHandles();
        this.updateVisibility();
        this.notifyState('pose-ready');
    }

    setModelLoading(value) {
        this.modelLoading = Boolean(value);
        if (this.modelLoading) {
            this.cancelDrag();
            this.selectedLimb = '';
            this.selectedTrunk = '';
            this.hoveredTrunk = '';
            this.selectedRotationTarget = '';
            this.hoveredRotationTarget = '';
            this.fingerCurlSessions.clear();
            this.fingerSpreadSessions.clear();
        }
        this.updateVisibility();
    }

    setPoseMode(value) {
        this.poseMode = Boolean(value);
        if (!this.poseMode) {
            this.cancelDrag();
            this.selectedLimb = '';
            this.selectedTrunk = '';
            this.hoveredTrunk = '';
            this.selectedRotationTarget = '';
            this.hoveredRotationTarget = '';
            this.fingerCurlSessions.clear();
            this.fingerSpreadSessions.clear();
        }
        this.updateVisibility();
        if (this.poseMode) {
            this.notifyState('pose-ready');
        }
    }

    updateVisibility() {
        this.handleGroup.visible = this.poseMode && !this.modelLoading && this.bones.size > 0;
        for (const [endName, handle] of this.handles) {
            const limb = LIMBS[endName];
            const isAvailable = this.bones.has(limb.root) &&
                this.bones.has(limb.joint) &&
                this.bones.has(limb.end);
            handle.visible = isAvailable;
            this.handleHitTargets.get(endName).visible = isAvailable;
        }
        const selectedLimb = LIMBS[this.selectedLimb];
        const bendAvailable = Boolean(selectedLimb) &&
            this.bones.has(selectedLimb.root) &&
            this.bones.has(selectedLimb.joint) &&
            this.bones.has(selectedLimb.end);
        this.bendHandle.visible = this.handleGroup.visible && bendAvailable;
        this.bendHitTarget.visible = this.bendHandle.visible;
        for (const [name, handle] of this.trunkHandles) {
            const available = this.bones.has(TRUNK_HANDLES[name].bone) &&
                (name !== 'neck' || SPINE_BONES
                    .slice(0, -1)
                    .some((boneName) => this.bones.has(boneName)));
            handle.visible = this.handleGroup.visible && available;
            this.trunkHitTargets.get(name).visible = handle.visible;
        }
        for (const [boneName, handle] of this.rotationHandles) {
            const definition = ROTATION_TARGETS[boneName];
            const visible = this.handleGroup.visible &&
                this.bones.has(boneName) &&
                (!definition.selectionOnly || this.selectedRotationTarget === boneName);
            handle.visible = visible;
            for (const hit of this.rotationHitTargets.get(boneName)) {
                hit.visible = visible;
            }
        }
        this.headHitTarget.visible = this.handleGroup.visible && this.bones.has('head');
        for (const boneName of BODY_ROTATION_BONES) {
            const visible = this.handleGroup.visible && this.bones.has(boneName);
            this.bodyMarkers.get(boneName).visible = visible;
            this.bodyMarkerHitTargets.get(boneName).visible = visible;
        }
        this.bodyMarkerLink.visible = this.handleGroup.visible &&
            BODY_ROTATION_BONES.every((boneName) => this.bones.has(boneName));
        this.updateTrunkHandleAppearance();
        this.updateRotationHandleAppearance();
        this.updateBodyMarkerAppearance();
    }

    dispose() {
        this.cancelDrag();
        this.scene.remove(this.handleGroup);
        const geometries = new Set();
        const materials = new Set();
        for (const mesh of [...this.handles.values(), ...this.handleHitTargets.values()]) {
            geometries.add(mesh.geometry);
            materials.add(mesh.material);
        }
        geometries.add(this.bendHandle.geometry);
        geometries.add(this.bendHitTarget.geometry);
        materials.add(this.bendHandle.material);
        materials.add(this.bendHitTarget.material);
        for (const mesh of [...this.trunkHandles.values(), ...this.trunkHitTargets.values()]) {
            geometries.add(mesh.geometry);
            materials.add(mesh.material);
        }
        for (const group of this.rotationHandles.values()) {
            group.traverse((object) => {
                if (object.isMesh) {
                    geometries.add(object.geometry);
                    materials.add(object.material);
                }
            });
        }
        geometries.add(this.headHitTarget.geometry);
        materials.add(this.headHitTarget.material);
        for (const mesh of [...this.bodyMarkers.values(), ...this.bodyMarkerHitTargets.values()]) {
            geometries.add(mesh.geometry);
            materials.add(mesh.material);
        }
        geometries.add(this.bodyMarkerLink.geometry);
        materials.add(this.bodyMarkerLink.material);
        geometries.forEach((geometry) => geometry.dispose());
        materials.forEach((material) => material.dispose());
        this.handles.clear();
        this.handleHitTargets.clear();
        this.trunkHandles.clear();
        this.trunkHitTargets.clear();
        this.rotationHandles.clear();
        this.rotationHitTargets.clear();
        this.wristHandles.clear();
        this.wristHitTargets.clear();
        this.bodyMarkers.clear();
        this.bodyMarkerHitTargets.clear();
        this.explicitBendDirections.clear();
        this.explicitEffectorTargets.clear();
        this.explicitEndOrientations.clear();
        this.fingerCurlSessions.clear();
        this.fingerSpreadSessions.clear();
        this.fingerRig = null;
        this.fingerPoseStates = null;
        this.bones.clear();
        this.neutralRotations.clear();
        this.vrm = null;
    }

    setSelectedBone(name) {
        if (!this.bones.has(name)) {
            return false;
        }
        this.clearRotationSelection();
        this.selectedLimb = '';
        this.selectedTrunk = '';
        this.selectedBone = name;
        this.updateVisibility();
        this.notifyState('pose-changed');
        return true;
    }

    setBoneEuler(name, x, y, z) {
        const node = this.bones.get(name);
        const neutral = this.neutralRotations.get(name);
        if (!node || !neutral) {
            return false;
        }
        this.beginHistoryTransaction();
        const delta = eulerDegreesToQuaternion(x, y, z);
        node.quaternion.copy(deltaToLocal(neutral, delta));
        node.updateWorldMatrix(false, true);
        this.selectedBone = name;
        for (const side of HAND_SIDES) {
            rebaseFingerJoint(
                this.fingerRig?.[side],
                this.fingerPoseStates?.[side],
                name
            );
        }
        this.fingerCurlSessions.clear();
        this.fingerSpreadSessions.clear();
        this.rebuildIkStateFromCurrentPose();
        this.afterPoseChanged();
        this.commitHistoryTransaction();
        return true;
    }

    resetBone(name) {
        const node = this.bones.get(name);
        const neutral = this.neutralRotations.get(name);
        if (!node || !neutral) {
            return false;
        }
        this.beginHistoryTransaction();
        node.quaternion.copy(neutral);
        if (name === 'hips') {
            node.position.copy(this.neutralHipsPosition);
        }
        node.updateWorldMatrix(false, true);
        this.selectedBone = name;
        for (const side of HAND_SIDES) {
            rebaseFingerJoint(
                this.fingerRig?.[side],
                this.fingerPoseStates?.[side],
                name
            );
        }
        this.fingerCurlSessions.clear();
        this.fingerSpreadSessions.clear();
        this.rebuildIkStateFromCurrentPose();
        this.afterPoseChanged();
        this.commitHistoryTransaction();
        return true;
    }

    resetAll() {
        this.cancelDrag();
        this.beginHistoryTransaction();
        for (const [name, node] of this.bones) {
            node.quaternion.copy(this.neutralRotations.get(name));
        }
        const hips = this.bones.get('hips');
        if (hips) {
            hips.position.copy(this.neutralHipsPosition);
        }
        this.vrm?.scene?.updateWorldMatrix(true, true);
        this.explicitBendDirections.clear();
        this.explicitEffectorTargets.clear();
        this.explicitEndOrientations.clear();
        for (const side of HAND_SIDES) {
            resetFingerPoseState(
                this.fingerRig?.[side],
                this.fingerPoseStates?.[side]
            );
        }
        this.fingerCurlSessions.clear();
        this.fingerSpreadSessions.clear();
        this.selectedLimb = '';
        this.selectedTrunk = '';
        this.hoveredTrunk = '';
        this.selectedRotationTarget = '';
        this.hoveredRotationTarget = '';
        this.selectedBone = this.bones.has('hips')
            ? 'hips'
            : this.bones.keys().next().value ?? '';
        this.rebuildEndOrientationsFromCurrentPose();
        this.afterPoseChanged();
        this.updateVisibility();
        this.commitHistoryTransaction();
    }

    selectPoseHand(side) {
        if (!this.poseMode ||
            !HAND_SIDES.includes(side) ||
            !this.fingerRig?.[side]?.available) {
            return false;
        }
        this.selectedHand = side;
        this.clearRotationSelection();
        this.selectedLimb = '';
        this.selectedTrunk = '';
        this.updateVisibility();
        this.notifyState('pose-changed');
        return true;
    }

    selectPoseFinger(side, finger) {
        const joints = this.fingerRig?.[side]?.fingers?.[finger];
        if (!this.poseMode || !joints?.length) {
            return false;
        }
        this.selectedHand = side;
        this.clearRotationSelection();
        this.selectedLimb = '';
        this.selectedTrunk = '';
        this.selectedBone = joints[0].name;
        this.updateVisibility();
        this.notifyState('pose-changed');
        return true;
    }

    beginFingerCurl(side, finger) {
        const joints = this.fingerRig?.[side]?.fingers?.[finger];
        const state = this.fingerPoseStates?.[side];
        if (!this.poseMode || !joints?.length || !state || this.modelLoading) {
            return false;
        }
        this.beginHistoryTransaction();
        this.selectedHand = side;
        this.clearRotationSelection();
        this.selectedLimb = '';
        this.selectedTrunk = '';
        this.selectedBone = joints[0].name;
        this.updateVisibility();
        this.fingerCurlSessions.set(
            `${side}:${finger}`,
            { startAmount: state.curls[finger] }
        );
        this.notifyState('pose-changed');
        return true;
    }

    setFingerCurl(side, finger, amount) {
        const joints = this.fingerRig?.[side]?.fingers?.[finger];
        const state = this.fingerPoseStates?.[side];
        if (!this.poseMode || !joints?.length || !state || this.modelLoading) {
            return false;
        }
        const changed = applyFingerCurlState(
            this.fingerRig[side],
            state,
            finger,
            amount
        );
        if (changed) {
            this.selectedHand = side;
            this.selectedBone = joints[0].name;
            this.vrm?.scene?.updateWorldMatrix(true, true);
            this.afterPoseChanged(true);
        }
        return changed;
    }

    endFingerCurl(side, finger) {
        const removed = this.fingerCurlSessions.delete(`${side}:${finger}`);
        if (removed) {
            this.afterPoseChanged();
            this.commitHistoryTransaction();
        }
        return removed;
    }

    beginFingerSpread(side) {
        const state = this.fingerPoseStates?.[side];
        if (!this.poseMode ||
            !this.fingerRig?.[side]?.available ||
            !state ||
            this.modelLoading) {
            return false;
        }
        this.beginHistoryTransaction();
        this.clearRotationSelection();
        this.selectedLimb = '';
        this.selectedTrunk = '';
        this.updateVisibility();
        this.fingerSpreadSessions.set(
            side,
            { startAmount: state.fingerSpread }
        );
        return true;
    }

    setFingerSpread(side, amount) {
        const state = this.fingerPoseStates?.[side];
        if (!this.poseMode ||
            !this.fingerRig?.[side]?.available ||
            !state ||
            this.modelLoading) {
            return false;
        }
        const changed = applyFingerSpreadState(
            this.fingerRig[side],
            state,
            amount
        );
        if (changed) {
            this.selectedHand = side;
            this.vrm?.scene?.updateWorldMatrix(true, true);
            this.afterPoseChanged(true);
        }
        return changed;
    }

    endFingerSpread(side) {
        const removed = this.fingerSpreadSessions.delete(side);
        if (removed) {
            this.afterPoseChanged();
            this.commitHistoryTransaction();
        }
        return removed;
    }

    copyFingersToOtherHand(sourceSide) {
        const targetSide = sourceSide === 'left' ? 'right' : 'left';
        if (!this.poseMode ||
            !this.fingerRig?.[sourceSide]?.available ||
            !this.fingerRig?.[targetSide]?.available) {
            return false;
        }
        this.beginHistoryTransaction();
        const sourceState = this.fingerPoseStates[sourceSide];
        const targetState = this.fingerPoseStates[targetSide];
        let changed = resetFingerPoseState(this.fingerRig[targetSide], targetState);
        for (const finger of FINGER_NAMES) {
            const targetJoints = this.fingerRig[targetSide].fingers[finger];
            const sourceJoints = this.fingerRig[sourceSide].fingers[finger];
            if (!sourceJoints.length || !targetJoints.length) {
                continue;
            }
            changed = applyFingerCurlState(
                this.fingerRig[targetSide],
                targetState,
                finger,
                sourceState.curls[finger]
            ) || changed;
        }
        changed = applyFingerSpreadState(
            this.fingerRig[targetSide],
            targetState,
            sourceState.fingerSpread
        ) || changed;
        if (changed) {
            this.selectedHand = targetSide;
            this.fingerCurlSessions.clear();
            this.fingerSpreadSessions.clear();
            this.vrm?.scene?.updateWorldMatrix(true, true);
            this.afterPoseChanged();
        }
        this.commitHistoryTransaction();
        return changed;
    }

    resetHandFingers(side) {
        if (!this.poseMode || !this.fingerRig?.[side]?.available) {
            return false;
        }
        this.beginHistoryTransaction();
        const changed = resetFingerPoseState(
            this.fingerRig[side],
            this.fingerPoseStates?.[side]
        );
        if (changed) {
            this.selectedHand = side;
            this.fingerCurlSessions.clear();
            this.fingerSpreadSessions.clear();
            this.vrm?.scene?.updateWorldMatrix(true, true);
            this.rebuildIkStateFromCurrentPose();
            this.afterPoseChanged();
        }
        this.commitHistoryTransaction();
        return changed;
    }

    getCurrentPose() {
        const bones = {};
        for (const [name, node] of this.bones) {
            const delta = neutralToDelta(this.neutralRotations.get(name), node.quaternion);
            bones[name] = {
                rotation: {
                    x: delta.x,
                    y: delta.y,
                    z: delta.z,
                    w: delta.w
                }
            };
        }
        return {
            format: 'AvataRunwayPose',
            version: 2,
            rotationSpace: 'normalized-local-delta',
            eulerOrder: EULER_ORDER,
            translationSpace: 'normalized-hips-local-height-ratio',
            hipsTranslation: this.getNormalizedHipsTranslation(),
            bones
        };
    }

    applyPose(pose, { recordHistory = true } = {}) {
        const parsed = typeof pose === 'string' ? JSON.parse(pose) : pose;
        const entries = Object.entries(parsed?.bones ?? {});
        const rotations = entries.map(([name, bone]) => [
            name,
            normalizeQuaternionValue(bone?.rotation)
        ]);
        let hipsTranslation = null;
        if (parsed?.version >= 2) {
            if (parsed.version !== 2 ||
                parsed.translationSpace !== 'normalized-hips-local-height-ratio' ||
                !parsed.hipsTranslation) {
                throw new TypeError('Pose hips translation metadata is unsupported or missing.');
            }
            hipsTranslation = new THREE.Vector3(
                Number(parsed.hipsTranslation.x),
                Number(parsed.hipsTranslation.y),
                Number(parsed.hipsTranslation.z)
            );
            if (!hipsTranslation.toArray().every(Number.isFinite)) {
                throw new TypeError('Pose hips translation must be finite.');
            }
        }
        if (recordHistory) {
            this.beginHistoryTransaction();
        }
        for (const [name, node] of this.bones) {
            node.quaternion.copy(this.neutralRotations.get(name));
        }
        const ignoredBones = [];
        for (const [name, delta] of rotations) {
            const node = this.bones.get(name);
            const neutral = this.neutralRotations.get(name);
            if (!node || !neutral) {
                ignoredBones.push(name);
                continue;
            }
            node.quaternion.copy(deltaToLocal(neutral, delta));
        }
        const hips = this.bones.get('hips');
        if (hips) {
            hips.position.copy(this.neutralHipsPosition);
            if (hipsTranslation) {
                hips.position.addScaledVector(hipsTranslation, this.avatarHeight);
            }
        }
        this.vrm?.scene?.updateWorldMatrix(true, true);
        for (const side of HAND_SIDES) {
            synchronizeFingerPoseState(
                this.fingerRig?.[side],
                this.fingerPoseStates?.[side]
            );
        }
        this.rebuildIkStateFromCurrentPose();
        this.afterPoseChanged();
        if (recordHistory) {
            this.commitHistoryTransaction();
        }
        return ignoredBones;
    }

    beginHandleDrag(event, camera, domElement) {
        if (!this.handleGroup.visible || event.button !== 0 || event.shiftKey) {
            return false;
        }
        const pointer = this.pointerNdc(event, domElement);
        this.raycaster.setFromCamera(pointer, camera);
        const rotationIntersection = this.raycaster.intersectObjects(
            [...this.rotationHitTargets.values()].flat(),
            false
        ).find((hit) =>
            hit.object.visible &&
            ROTATION_TARGETS[hit.object.userData.poseRotationTarget]?.selectionOnly
        );
        if (rotationIntersection) {
            return this.beginRotationDrag(
                event,
                camera,
                domElement,
                rotationIntersection.object.userData.poseRotationTarget,
                rotationIntersection.object.userData.poseRotationAxis
            );
        }
        const bodyMarkerIntersection = this.raycaster.intersectObjects(
            [...this.bodyMarkerHitTargets.values()],
            false
        ).find((hit) => hit.object.visible);
        if (bodyMarkerIntersection) {
            return this.selectRotationTarget(
                event,
                bodyMarkerIntersection.object.userData.poseBodyBone
            );
        }
        const trunkIntersection = this.raycaster.intersectObjects(
            [...this.trunkHitTargets.values()],
            false
        ).find((hit) => hit.object.visible);
        if (trunkIntersection) {
            return this.beginTrunkDrag(
                event,
                camera,
                trunkIntersection.object.userData.poseTrunkHandle
            );
        }
        if (this.bendHitTarget.visible) {
            const bendIntersection = this.raycaster.intersectObject(this.bendHitTarget, false)[0];
            if (bendIntersection) {
                return this.beginBendDrag(event, camera);
            }
        }
        const intersections = this.raycaster.intersectObjects([...this.handleHitTargets.values()], false);
        const intersection = intersections.find((hit) => hit.object.visible);
        if (!intersection) {
            const endpointRotationIntersection = this.raycaster.intersectObjects(
                [...this.rotationHitTargets.values()].flat(),
                false
            ).find((hit) => hit.object.visible);
            if (endpointRotationIntersection) {
                return this.beginRotationDrag(
                    event,
                    camera,
                    domElement,
                    endpointRotationIntersection.object.userData.poseRotationTarget,
                    endpointRotationIntersection.object.userData.poseRotationAxis
                );
            }
            const headIntersection = this.headHitTarget.visible
                ? this.raycaster.intersectObject(this.headHitTarget, false)[0]
                : null;
            if (headIntersection) {
                return this.selectRotationTarget(event, 'head');
            }
            this.selectedLimb = '';
            this.selectedTrunk = '';
            this.clearRotationSelection();
            this.updateVisibility();
            return false;
        }

        const endName = intersection.object.userData.poseEndBone;
        const limb = LIMBS[endName];
        const root = this.bones.get(limb.root);
        const joint = this.bones.get(limb.joint);
        const end = this.bones.get(limb.end);
        if (!root || !joint || !end) {
            return false;
        }

        this.vrm.scene.updateWorldMatrix(true, true);
        const rootPosition = root.getWorldPosition(new THREE.Vector3());
        const jointPosition = joint.getWorldPosition(new THREE.Vector3());
        const endPosition = end.getWorldPosition(new THREE.Vector3());
        const chainDirection = endPosition.clone().sub(rootPosition).normalize();
        const currentBend = jointPosition.clone().sub(rootPosition);
        currentBend.addScaledVector(chainDirection, -currentBend.dot(chainDirection));
        const modelWorld = this.vrm.scene.getWorldQuaternion(new THREE.Quaternion());
        const fallback = limb.fallbackBend.clone().applyQuaternion(modelWorld);
        const explicitBend = this.explicitBendDirections.get(endName);
        const preferredBend = explicitBend
            ? stablePerpendicular(chainDirection, explicitBend)
            : currentBend.lengthSq() > 1e-10
                ? currentBend.normalize()
                : stablePerpendicular(chainDirection, fallback);

        const cameraDirection = camera.getWorldDirection(new THREE.Vector3());
        this.dragPlane.setFromNormalAndCoplanarPoint(cameraDirection, endPosition);
        const planePoint = this.raycaster.ray.intersectPlane(this.dragPlane, new THREE.Vector3()) ?? endPosition;
        this.drag = {
            kind: 'effector',
            pointerId: event.pointerId,
            endName,
            limb,
            preferredBend,
            offset: endPosition.clone().sub(planePoint)
        };
        this.beginHistoryTransaction();
        this.explicitEffectorTargets.set(endName, endPosition.clone());
        this.selectedLimb = endName;
        this.selectedTrunk = '';
        this.clearRotationSelection();
        this.selectedBone = limb.joint;
        this.updateHandles();
        this.updateVisibility();
        return true;
    }

    selectHead(event) {
        return this.selectRotationTarget(event, 'head');
    }

    selectRotationTarget(event, boneName) {
        if (!ROTATION_TARGETS[boneName] || !this.bones.has(boneName)) {
            return false;
        }
        this.cancelDrag();
        this.selectedRotationTarget = boneName;
        this.hoveredRotationTarget = '';
        this.selectedLimb = '';
        this.selectedTrunk = '';
        this.selectedBone = boneName;
        this.drag = {
            kind: 'selection',
            pointerId: event.pointerId
        };
        this.beginHistoryTransaction();
        this.updateVisibility();
        this.notifyState('pose-changed');
        return true;
    }

    beginRotationDrag(event, camera, domElement, boneName, axis) {
        const node = this.bones.get(boneName);
        if (!node || !ROTATION_TARGETS[boneName]) {
            return false;
        }
        this.vrm.scene.updateWorldMatrix(true, true);
        const position = this.rotationHandles.get(boneName)?.position.clone() ??
            node.getWorldPosition(new THREE.Vector3());
        const projected = position.clone().project(camera);
        const rect = domElement.getBoundingClientRect();
        const center = new THREE.Vector2(
            rect.left + (projected.x + 1) * 0.5 * rect.width,
            rect.top + (1 - projected.y) * 0.5 * rect.height
        );
        const startVector = this.trackballVector(event, center);
        if (!startVector) {
            return false;
        }
        const parentWorld = node.parent
            ? node.parent.getWorldQuaternion(new THREE.Quaternion())
            : new THREE.Quaternion();
        this.drag = {
            kind: 'rotation',
            pointerId: event.pointerId,
            boneName,
            axis,
            center,
            startVector,
            startWorld: node.getWorldQuaternion(new THREE.Quaternion()),
            parentWorld,
            cameraWorld: camera.getWorldQuaternion(new THREE.Quaternion())
        };
        this.beginHistoryTransaction();
        this.selectedRotationTarget = boneName;
        this.hoveredRotationTarget = '';
        this.selectedLimb = '';
        this.selectedTrunk = '';
        const side = ROTATION_TARGETS[boneName].side;
        if (side && (boneName.endsWith('Hand'))) {
            this.selectedHand = side;
        }
        this.selectedBone = boneName;
        this.updateVisibility();
        this.updateRotationHandleAppearance();
        this.notifyState('pose-changed');
        return true;
    }

    beginWristDrag(event, camera, domElement, endName, axis) {
        return this.beginRotationDrag(event, camera, domElement, endName, axis);
    }

    beginTrunkDrag(event, camera, name) {
        const definition = TRUNK_HANDLES[name];
        const node = this.bones.get(definition?.bone);
        if (!node) {
            return false;
        }
        this.vrm.scene.updateWorldMatrix(true, true);
        const handlePosition = node.getWorldPosition(new THREE.Vector3());
        const cameraDirection = camera.getWorldDirection(new THREE.Vector3());
        this.dragPlane.setFromNormalAndCoplanarPoint(cameraDirection, handlePosition);
        const planePoint = this.raycaster.ray.intersectPlane(this.dragPlane, new THREE.Vector3()) ??
            handlePosition;
        const drag = {
            kind: name,
            pointerId: event.pointerId,
            offset: handlePosition.clone().sub(planePoint),
            handTargets: this.captureExplicitHandTargets(),
            bendDirections: this.captureBendDirections()
        };

        if (name === 'hips') {
            const hipsPosition = this.bones.get('hips').getWorldPosition(new THREE.Vector3());
            drag.footTargets = this.captureTargets(['leftFoot', 'rightFoot']);
            drag.pelvisConstraints = [];
            for (const endName of ['leftFoot', 'rightFoot']) {
                const limb = LIMBS[endName];
                const root = this.bones.get(limb.root);
                const joint = this.bones.get(limb.joint);
                const end = this.bones.get(limb.end);
                const target = drag.footTargets.get(endName);
                if (!root || !joint || !end || !target) {
                    continue;
                }
                const rootPosition = root.getWorldPosition(new THREE.Vector3());
                const jointPosition = joint.getWorldPosition(new THREE.Vector3());
                const endPosition = end.getWorldPosition(new THREE.Vector3());
                const maximumLength = rootPosition.distanceTo(jointPosition) +
                    jointPosition.distanceTo(endPosition) - 1e-6;
                const minimumLength = Math.abs(
                    rootPosition.distanceTo(jointPosition) -
                    jointPosition.distanceTo(endPosition)
                ) + 1e-6;
                drag.pelvisConstraints.push({
                    rootOffset: rootPosition.clone().sub(hipsPosition),
                    target,
                    minimumLength,
                    maximumLength,
                    minimumDirection: rootPosition.clone().sub(target)
                });
            }
        } else {
            drag.spineChain = SPINE_BONES
                .map((boneName) => this.bones.get(boneName))
                .filter(Boolean);
            if (drag.spineChain.length < 2 ||
                drag.spineChain.at(-1) !== this.bones.get('neck')) {
                return false;
            }
        }

        this.drag = drag;
        this.beginHistoryTransaction();
        this.selectedTrunk = name;
        this.selectedLimb = '';
        this.clearRotationSelection();
        this.selectedBone = definition.bone;
        this.updateVisibility();
        return true;
    }

    beginBendDrag(event, camera) {
        const endName = this.selectedLimb;
        const limb = LIMBS[endName];
        const root = this.bones.get(limb?.root);
        const joint = this.bones.get(limb?.joint);
        const end = this.bones.get(limb?.end);
        if (!root || !joint || !end) {
            return false;
        }

        this.vrm.scene.updateWorldMatrix(true, true);
        const jointPosition = joint.getWorldPosition(new THREE.Vector3());
        const endPosition = end.getWorldPosition(new THREE.Vector3());
        const cameraDirection = camera.getWorldDirection(new THREE.Vector3());
        this.dragPlane.setFromNormalAndCoplanarPoint(cameraDirection, jointPosition);
        const planePoint = this.raycaster.ray.intersectPlane(this.dragPlane, new THREE.Vector3()) ??
            jointPosition;
        this.drag = {
            kind: 'bend',
            pointerId: event.pointerId,
            endName,
            limb,
            effectorTarget: endPosition,
            offset: jointPosition.clone().sub(planePoint)
        };
        this.beginHistoryTransaction();
        this.selectedTrunk = '';
        this.clearRotationSelection();
        this.selectedBone = limb.joint;
        this.updateTrunkHandleAppearance();
        return true;
    }

    moveHandleDrag(event, camera, domElement) {
        if (!this.drag || event.pointerId !== this.drag.pointerId) {
            return false;
        }
        const pointer = this.pointerNdc(event, domElement);
        this.raycaster.setFromCamera(pointer, camera);
        if (this.drag.kind === 'selection') {
            return true;
        }
        if (this.drag.kind === 'rotation') {
            return this.moveRotationDrag(event);
        }
        const point = this.raycaster.ray.intersectPlane(this.dragPlane, new THREE.Vector3());
        if (!point) {
            return true;
        }
        const { limb } = this.drag;
        let solution;
        if (this.drag.kind === 'hips') {
            solution = this.solveHipsTarget(point.add(this.drag.offset), this.drag);
        } else if (this.drag.kind === 'neck') {
            solution = this.solveNeckTarget(point.add(this.drag.offset), this.drag);
        } else if (this.drag.kind === 'bend') {
            const root = this.bones.get(limb.root);
            root.updateWorldMatrix(true, true);
            const rootPosition = root.getWorldPosition(new THREE.Vector3());
            const chainDirection = this.drag.effectorTarget.clone().sub(rootPosition);
            const bendHint = point.add(this.drag.offset).sub(rootPosition);
            const bendDirection = stablePerpendicular(chainDirection.normalize(), bendHint);
            solution = this.solveLimbTarget(
                this.drag.endName,
                this.drag.effectorTarget,
                bendDirection
            );
            if (solution) {
                this.explicitBendDirections.set(this.drag.endName, solution.bendDirection.clone());
            }
        } else {
            solution = this.solveLimbTarget(
                this.drag.endName,
                point.add(this.drag.offset),
                this.drag.preferredBend
            );
            if (solution && this.explicitBendDirections.has(this.drag.endName)) {
                this.explicitBendDirections.set(
                    this.drag.endName,
                    solution.bendDirection.clone()
                );
            }
            if (solution) {
                this.explicitEffectorTargets.set(
                    this.drag.endName,
                    solution.target.clone()
                );
            }
        }
        if (solution) {
            this.afterPoseChanged(true);
        }
        return true;
    }

    moveRotationDrag(event) {
        const node = this.bones.get(this.drag.boneName);
        const currentVector = this.trackballVector(event, this.drag.center);
        if (!node || !currentVector) {
            return true;
        }
        const cameraDelta = new THREE.Quaternion().setFromUnitVectors(
            this.drag.startVector,
            currentVector
        );
        const worldDelta = this.drag.cameraWorld.clone()
            .multiply(cameraDelta)
            .multiply(this.drag.cameraWorld.clone().invert())
            .normalize();
        const worldRotation = worldDelta.multiply(this.drag.startWorld).normalize();
        const localRotation = this.drag.parentWorld.clone().invert()
            .multiply(worldRotation)
            .normalize();
        if (!isFiniteQuaternion(localRotation)) {
            return true;
        }
        node.quaternion.copy(localRotation);
        node.updateWorldMatrix(false, true);
        if (ROTATION_TARGETS[this.drag.boneName]?.preserveAfterIk) {
            this.explicitEndOrientations.set(
                this.drag.boneName,
                node.getWorldQuaternion(new THREE.Quaternion())
            );
        }
        this.afterPoseChanged(true);
        return true;
    }

    solveHipsTarget(requestedWorldPosition, drag) {
        const snapshot = this.captureTransforms();
        try {
            const constrained = constrainPelvisPosition(
                requestedWorldPosition,
                drag.pelvisConstraints
            );
            this.setBoneWorldPosition(this.bones.get('hips'), constrained);
            this.vrm.scene.updateWorldMatrix(true, true);
            for (const endName of ['leftFoot', 'rightFoot']) {
                const target = drag.footTargets.get(endName);
                if (target) {
                    if (!this.solveLimbTarget(
                        endName,
                        target,
                        drag.bendDirections.get(endName))) {
                        throw new Error(`Failed to solve ${endName} after pelvis movement.`);
                    }
                }
            }
            const footTolerance = Math.max(this.avatarHeight * 1e-4, 1e-5);
            for (const [endName, target] of drag.footTargets) {
                const end = this.bones.get(LIMBS[endName]?.end);
                if (end &&
                    end.getWorldPosition(new THREE.Vector3()).distanceTo(target) >
                        footTolerance) {
                    throw new Error(`${endName} moved outside the fixed-target tolerance.`);
                }
            }
            for (const [endName, target] of drag.handTargets) {
                if (!this.solveLimbTarget(endName, target, drag.bendDirections.get(endName))) {
                    throw new Error(`Failed to solve ${endName} after pelvis movement.`);
                }
            }
            if (!this.transformsAreFinite()) {
                throw new Error('Pelvis IK produced a non-finite transform.');
            }
            return true;
        } catch {
            this.restoreTransforms(snapshot);
            return false;
        }
    }

    solveNeckTarget(target, drag) {
        const snapshot = this.captureTransforms();
        try {
            const result = solveSpineCcd(drag.spineChain, target, {
                maximumIterations: 12,
                maximumStepDegrees: 12,
                tolerance: Math.max(this.avatarHeight * 0.002, 0.0005)
            });
            for (const [endName, handTarget] of drag.handTargets) {
                if (!this.solveLimbTarget(
                    endName,
                    handTarget,
                    drag.bendDirections.get(endName))) {
                    throw new Error(`Failed to solve ${endName} after spine movement.`);
                }
            }
            if (!Number.isFinite(result.distance) || !this.transformsAreFinite()) {
                throw new Error('Spine IK produced a non-finite transform.');
            }
            return true;
        } catch {
            this.restoreTransforms(snapshot);
            return false;
        }
    }

    endHandleDrag(pointerId) {
        if (!this.drag || this.drag.pointerId !== pointerId) {
            return false;
        }
        this.drag = null;
        this.updateRotationHandleAppearance();
        this.updateBodyMarkerAppearance();
        this.afterPoseChanged();
        this.commitHistoryTransaction();
        return true;
    }

    cancelDrag() {
        const hadTransaction = Boolean(this.historyTransaction);
        this.drag = null;
        this.fingerCurlSessions.clear();
        this.fingerSpreadSessions.clear();
        this.updateRotationHandleAppearance();
        this.updateBodyMarkerAppearance();
        if (hadTransaction) {
            this.commitHistoryTransaction();
        }
    }

    updateHover(event, camera, domElement) {
        if (!this.handleGroup.visible || this.drag || !camera) {
            if (this.hoveredTrunk) {
                this.hoveredTrunk = '';
                this.updateTrunkHandleAppearance();
            }
            if (this.hoveredRotationTarget) {
                this.hoveredRotationTarget = '';
                this.updateRotationHandleAppearance();
                this.updateBodyMarkerAppearance();
            }
            return;
        }
        this.raycaster.setFromCamera(this.pointerNdc(event, domElement), camera);
        const rotationHit = this.raycaster.intersectObjects(
            [...this.rotationHitTargets.values()].flat(),
            false
        ).find((intersection) => intersection.object.visible);
        const bodyMarkerHit = this.raycaster.intersectObjects(
            [...this.bodyMarkerHitTargets.values()],
            false
        ).find((intersection) => intersection.object.visible);
        const hoveredRotationTarget =
            rotationHit?.object.userData.poseRotationTarget ??
            bodyMarkerHit?.object.userData.poseBodyBone ??
            '';
        if (hoveredRotationTarget !== this.hoveredRotationTarget) {
            this.hoveredRotationTarget = hoveredRotationTarget;
            this.updateRotationHandleAppearance();
            this.updateBodyMarkerAppearance();
        }
        const hit = this.raycaster.intersectObjects(
            [...this.trunkHitTargets.values()],
            false
        ).find((intersection) => intersection.object.visible);
        const hovered = hit?.object.userData.poseTrunkHandle ?? '';
        if (hovered !== this.hoveredTrunk) {
            this.hoveredTrunk = hovered;
            this.updateTrunkHandleAppearance();
        }
    }

    clearHover() {
        if (this.hoveredTrunk) {
            this.hoveredTrunk = '';
            this.updateTrunkHandleAppearance();
        }
        if (this.hoveredRotationTarget) {
            this.hoveredRotationTarget = '';
            this.updateRotationHandleAppearance();
            this.updateBodyMarkerAppearance();
        }
    }

    updateTrunkHandleAppearance() {
        for (const [name, handle] of this.trunkHandles) {
            const emphasized = name === this.selectedTrunk || name === this.hoveredTrunk;
            handle.material.color.setHex(emphasized ? 0xffff66 : TRUNK_HANDLES[name].color);
            handle.material.opacity = emphasized ? 1 : 0.95;
            handle.userData.poseEmphasized = emphasized;
        }
    }

    updateRotationHandleAppearance() {
        for (const [boneName, group] of this.rotationHandles) {
            const emphasized = boneName === this.selectedRotationTarget ||
                boneName === this.hoveredRotationTarget ||
                (this.drag?.kind === 'rotation' && this.drag.boneName === boneName);
            group.userData.poseEmphasized = emphasized;
            group.traverse((object) => {
                if (!object.isMesh || object.material.colorWrite === false) {
                    return;
                }
                object.material.color.setHex(
                    emphasized ? ROTATION_HANDLE_ACTIVE_COLOR : ROTATION_HANDLE_COLOR
                );
                object.material.opacity = emphasized ? 1 : 0.88;
            });
        }
    }

    updateBodyMarkerAppearance() {
        for (const [boneName, marker] of this.bodyMarkers) {
            const emphasized = boneName === this.selectedRotationTarget ||
                boneName === this.hoveredRotationTarget ||
                (this.drag?.kind === 'rotation' && this.drag.boneName === boneName);
            marker.userData.poseEmphasized = emphasized;
            marker.material.color.setHex(
                emphasized ? BODY_MARKER_ACTIVE_COLOR : BODY_MARKER_COLOR
            );
            marker.material.opacity = emphasized ? 1 : 0.62;
        }
    }

    clearRotationSelection() {
        this.selectedRotationTarget = '';
        this.hoveredRotationTarget = '';
        this.updateRotationHandleAppearance();
        this.updateBodyMarkerAppearance();
    }

    captureTargets(endNames) {
        const targets = new Map();
        this.vrm?.scene?.updateWorldMatrix(true, true);
        for (const endName of endNames) {
            const node = this.bones.get(LIMBS[endName]?.end);
            if (node) {
                targets.set(endName, node.getWorldPosition(new THREE.Vector3()));
            }
        }
        return targets;
    }

    captureExplicitHandTargets() {
        const targets = new Map();
        for (const endName of ['leftHand', 'rightHand']) {
            const target = this.explicitEffectorTargets.get(endName);
            if (target) {
                targets.set(endName, target.clone());
            }
        }
        return targets;
    }

    captureBendDirections() {
        const directions = new Map();
        for (const endName of Object.keys(LIMBS)) {
            const direction = this.explicitBendDirections.get(endName) ??
                this.currentBendDirection(endName);
            if (direction) {
                directions.set(endName, direction.clone());
            }
        }
        return directions;
    }

    solveLimbTarget(endName, target, preferredBend = null) {
        const limb = LIMBS[endName];
        const root = this.bones.get(limb?.root);
        const joint = this.bones.get(limb?.joint);
        const end = this.bones.get(limb?.end);
        if (!root || !joint || !end) {
            return null;
        }
        const solution = applyTwoBoneIk(root, joint, end, target, preferredBend);
        if (solution && ROTATION_TARGETS[endName]?.preserveAfterIk) {
            this.applyExplicitEndOrientation(endName);
        }
        return solution;
    }

    captureTransforms() {
        return {
            rotations: new Map([...this.bones].map(([name, node]) => [
                name,
                node.quaternion.clone()
            ])),
            hipsPosition: this.bones.get('hips')?.position.clone() ?? null
        };
    }

    restoreTransforms(snapshot) {
        for (const [name, quaternion] of snapshot.rotations) {
            this.bones.get(name)?.quaternion.copy(quaternion);
        }
        if (snapshot.hipsPosition) {
            this.bones.get('hips')?.position.copy(snapshot.hipsPosition);
        }
        this.vrm?.scene?.updateWorldMatrix(true, true);
    }

    transformsAreFinite() {
        const rotationsFinite = [...this.bones.values()].every((node) =>
            node.quaternion.toArray().every(Number.isFinite));
        const hips = this.bones.get('hips');
        return rotationsFinite && (!hips || hips.position.toArray().every(Number.isFinite));
    }

    setBoneWorldPosition(node, worldPosition) {
        const localPosition = worldPosition.clone();
        if (node.parent) {
            node.parent.updateWorldMatrix(true, false);
            node.parent.worldToLocal(localPosition);
        }
        node.position.copy(localPosition);
        node.updateWorldMatrix(false, true);
    }

    setBoneWorldQuaternion(node, worldQuaternion) {
        const local = worldQuaternion.clone();
        if (node.parent) {
            node.parent.updateWorldMatrix(true, false);
            const parentWorld = node.parent.getWorldQuaternion(new THREE.Quaternion());
            local.premultiply(parentWorld.invert());
        }
        if (isFiniteQuaternion(local)) {
            node.quaternion.copy(local.normalize());
            node.updateWorldMatrix(false, true);
            return true;
        }
        return false;
    }

    applyExplicitEndOrientation(endName) {
        const node = this.bones.get(endName);
        const orientation = this.explicitEndOrientations.get(endName);
        return Boolean(node && orientation && this.setBoneWorldQuaternion(node, orientation));
    }

    rebuildEndOrientationsFromCurrentPose() {
        this.explicitEndOrientations.clear();
        this.vrm?.scene?.updateWorldMatrix(true, true);
        for (const [endName, definition] of Object.entries(ROTATION_TARGETS)) {
            if (!definition.preserveAfterIk) {
                continue;
            }
            const node = this.bones.get(endName);
            if (node) {
                this.explicitEndOrientations.set(
                    endName,
                    node.getWorldQuaternion(new THREE.Quaternion())
                );
            }
        }
    }

    computeAvatarHeight() {
        const root = this.vrm?.humanoid?.normalizedHumanBonesRoot;
        if (!root || this.bones.size === 0) {
            return 1;
        }
        root.updateWorldMatrix(true, true);
        let minimumY = Number.POSITIVE_INFINITY;
        let maximumY = Number.NEGATIVE_INFINITY;
        for (const node of this.bones.values()) {
            const local = root.worldToLocal(node.getWorldPosition(new THREE.Vector3()));
            minimumY = Math.min(minimumY, local.y);
            maximumY = Math.max(maximumY, local.y);
        }
        const height = maximumY - minimumY;
        return Number.isFinite(height) && height > 1e-4 ? height : 1;
    }

    getNormalizedHipsTranslation() {
        const hips = this.bones.get('hips');
        const delta = hips
            ? hips.position.clone().sub(this.neutralHipsPosition)
            : new THREE.Vector3();
        return {
            x: delta.x / this.avatarHeight,
            y: delta.y / this.avatarHeight,
            z: delta.z / this.avatarHeight
        };
    }

    currentBendDirection(endName) {
        const limb = LIMBS[endName];
        const root = this.bones.get(limb?.root);
        const joint = this.bones.get(limb?.joint);
        const end = this.bones.get(limb?.end);
        if (!root || !joint || !end) {
            return null;
        }
        const rootPosition = root.getWorldPosition(new THREE.Vector3());
        const jointPosition = joint.getWorldPosition(new THREE.Vector3());
        const endPosition = end.getWorldPosition(new THREE.Vector3());
        const chainDirection = endPosition.sub(rootPosition);
        if (chainDirection.lengthSq() <= 1e-12) {
            return null;
        }
        return stablePerpendicular(
            chainDirection.normalize(),
            jointPosition.sub(rootPosition)
        );
    }

    rebuildBendDirectionsFromCurrentPose() {
        this.vrm?.scene?.updateWorldMatrix(true, true);
        this.explicitBendDirections.clear();
        for (const endName of Object.keys(LIMBS)) {
            const direction = this.currentBendDirection(endName);
            if (direction) {
                this.explicitBendDirections.set(endName, direction);
            }
        }
    }

    rebuildIkStateFromCurrentPose() {
        this.rebuildBendDirectionsFromCurrentPose();
        this.rebuildEndOrientationsFromCurrentPose();
        this.fingerCurlSessions.clear();
        this.fingerSpreadSessions.clear();
        this.explicitEffectorTargets.clear();
        for (const [endName, target] of this.captureTargets(Object.keys(LIMBS))) {
            this.explicitEffectorTargets.set(endName, target);
        }
    }

    pointerNdc(event, domElement) {
        const rect = domElement.getBoundingClientRect();
        return new THREE.Vector2(
            ((event.clientX - rect.left) / Math.max(rect.width, 1)) * 2 - 1,
            -((event.clientY - rect.top) / Math.max(rect.height, 1)) * 2 + 1
        );
    }

    trackballVector(event, center) {
        const x = (event.clientX - center.x) / ROTATION_TRACKBALL_RADIUS_PIXELS;
        const y = -(event.clientY - center.y) / ROTATION_TRACKBALL_RADIUS_PIXELS;
        if (![x, y].every(Number.isFinite)) {
            return null;
        }
        const lengthSq = x * x + y * y;
        if (lengthSq <= 1) {
            return new THREE.Vector3(x, y, Math.sqrt(Math.max(0, 1 - lengthSq))).normalize();
        }
        return new THREE.Vector3(x, y, 0).normalize();
    }

    beginHistoryTransaction() {
        if (!this.vrm || this.historyTransaction) {
            return;
        }
        this.historyTransaction = this.getCurrentPose();
    }

    commitHistoryTransaction() {
        const before = this.historyTransaction;
        this.historyTransaction = null;
        if (!before || !this.vrm) {
            return false;
        }
        const after = this.getCurrentPose();
        if (JSON.stringify(before) === JSON.stringify(after)) {
            return false;
        }
        this.undoStack.push(before);
        if (this.undoStack.length > this.historyLimit) {
            this.undoStack.shift();
        }
        this.redoStack.length = 0;
        return true;
    }

    undo() {
        if (!this.poseMode ||
            this.modelLoading ||
            !this.undoStack.length ||
            this.drag ||
            !this.vrm) {
            return false;
        }
        const current = this.getCurrentPose();
        const previous = this.undoStack.pop();
        this.redoStack.push(current);
        this.applyPose(previous, { recordHistory: false });
        return true;
    }

    redo() {
        if (!this.poseMode ||
            this.modelLoading ||
            !this.redoStack.length ||
            this.drag ||
            !this.vrm) {
            return false;
        }
        const current = this.getCurrentPose();
        const next = this.redoStack.pop();
        this.undoStack.push(current);
        this.applyPose(next, { recordHistory: false });
        return true;
    }

    afterPoseChanged(throttle = false) {
        this.vrm?.scene?.updateWorldMatrix(true, true);
        this.updateHandles();
        if (!throttle) {
            if (this.notificationFrame) {
                cancelAnimationFrame(this.notificationFrame);
                this.notificationFrame = 0;
            }
            this.notifyState('pose-changed');
            return;
        }
        if (!this.notificationFrame) {
            this.notificationFrame = requestAnimationFrame(() => {
                this.notificationFrame = 0;
                this.notifyState('pose-changed');
            });
        }
    }

    notifyState(type) {
        const node = this.bones.get(this.selectedBone);
        const neutral = this.neutralRotations.get(this.selectedBone);
        const rotation = node && neutral
            ? quaternionToEulerDegrees(neutralToDelta(neutral, node.quaternion))
            : { x: 0, y: 0, z: 0 };
        this.notify({
            type,
            bones: [...this.bones.keys()],
            selectedBone: this.selectedBone,
            rotation,
            canUndo: this.undoStack.length > 0,
            canRedo: this.redoStack.length > 0,
            hands: {
                selectedHand: this.selectedHand,
                ...handPoseState(this.fingerRig, this.fingerPoseStates)
            }
        });
    }

    updateHandles() {
        if (!this.vrm) {
            return;
        }
        this.vrm.scene.updateWorldMatrix(true, true);
        for (const [endName, handle] of this.handles) {
            const node = this.bones.get(LIMBS[endName].end);
            if (node) {
                const position = node.getWorldPosition(new THREE.Vector3());
                handle.position.copy(position);
                this.handleHitTargets.get(endName).position.copy(position);
            }
        }
        const limb = LIMBS[this.selectedLimb];
        const joint = this.bones.get(limb?.joint);
        if (joint) {
            const position = joint.getWorldPosition(new THREE.Vector3());
            this.bendHandle.position.copy(position);
            this.bendHitTarget.position.copy(position);
        }
        for (const [name, handle] of this.trunkHandles) {
            const node = this.bones.get(TRUNK_HANDLES[name].bone);
            if (node) {
                const position = node.getWorldPosition(new THREE.Vector3());
                handle.position.copy(position);
                this.trunkHitTargets.get(name).position.copy(position);
            }
        }
        for (const boneName of BODY_ROTATION_BONES) {
            const node = this.bones.get(boneName);
            if (node) {
                const position = node.getWorldPosition(new THREE.Vector3());
                this.bodyMarkers.get(boneName).position.copy(position);
                this.bodyMarkerHitTargets.get(boneName).position.copy(position);
            }
        }
        if (BODY_ROTATION_BONES.every((boneName) => this.bones.has(boneName))) {
            const positions = this.bodyMarkerLink.geometry.attributes.position;
            for (const [index, boneName] of BODY_ROTATION_BONES.entries()) {
                const position = this.bones.get(boneName).getWorldPosition(new THREE.Vector3());
                positions.setXYZ(index, position.x, position.y, position.z);
            }
            positions.needsUpdate = true;
            this.bodyMarkerLink.geometry.computeBoundingSphere();
        }
        for (const [boneName, group] of this.rotationHandles) {
            const node = this.bones.get(boneName);
            if (node) {
                group.position.copy(node.getWorldPosition(new THREE.Vector3()));
                group.quaternion.copy(node.getWorldQuaternion(new THREE.Quaternion()));
            }
        }
        const head = this.bones.get('head');
        if (head) {
            const headPosition = head.getWorldPosition(new THREE.Vector3());
            const neck = this.bones.get('neck');
            let upward = neck
                ? headPosition.clone().sub(neck.getWorldPosition(new THREE.Vector3()))
                : new THREE.Vector3(0, 1, 0).applyQuaternion(
                    this.vrm.scene.getWorldQuaternion(new THREE.Quaternion())
                );
            let segmentLength = upward.length();
            if (segmentLength <= 1e-6) {
                upward.set(0, 1, 0);
                segmentLength = this.avatarHeight * 0.1;
            } else {
                upward.normalize();
            }
            const radius = THREE.MathUtils.clamp(
                Math.max(segmentLength * 0.68, this.avatarHeight * 0.06),
                this.avatarHeight * 0.05,
                this.avatarHeight * 0.12
            );
            this.headHitTarget.position.copy(headPosition)
                .addScaledVector(upward, segmentLength * 0.35);
            this.headHitTarget.scale.setScalar(radius);
            this.rotationHandles.get('head')?.position.copy(this.headHitTarget.position);
        }
    }

    updateHandleScale(camera, viewportHeight) {
        if (!this.handleGroup.visible || !camera) {
            return;
        }
        const safeHeight = Math.max(viewportHeight, 1);
        for (const [endName, handle] of this.handles) {
            let worldPerPixel;
            if (camera.isPerspectiveCamera) {
                const distance = camera.position.distanceTo(handle.position);
                worldPerPixel = 2 * distance *
                    Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / safeHeight;
            } else {
                worldPerPixel = (camera.top - camera.bottom) / (camera.zoom * safeHeight);
            }
            const scale = THREE.MathUtils.clamp(worldPerPixel * HANDLE_RADIUS_PIXELS, 0.008, 0.12);
            handle.scale.setScalar(scale);
            const hitScale = THREE.MathUtils.clamp(
                worldPerPixel * HANDLE_HIT_RADIUS_PIXELS,
                0.012,
                0.2
            );
            this.handleHitTargets.get(endName).scale.setScalar(hitScale);
        }
        if (this.bendHandle.visible) {
            let worldPerPixel;
            if (camera.isPerspectiveCamera) {
                const distance = camera.position.distanceTo(this.bendHandle.position);
                worldPerPixel = 2 * distance *
                    Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / safeHeight;
            } else {
                worldPerPixel = (camera.top - camera.bottom) / (camera.zoom * safeHeight);
            }
            const arrowScale = THREE.MathUtils.clamp(
                worldPerPixel * BEND_HANDLE_HALF_WIDTH_PIXELS,
                0.018,
                0.3
            );
            this.bendHandle.scale.setScalar(arrowScale);
            this.bendHitTarget.scale.setScalar(THREE.MathUtils.clamp(
                worldPerPixel * BEND_HANDLE_HIT_RADIUS_PIXELS,
                0.022,
                0.34
            ));

            const direction = this.explicitBendDirections.get(this.selectedLimb) ??
                this.currentBendDirection(this.selectedLimb);
            this.bendHandle.quaternion.copy(camera.quaternion);
            if (direction) {
                const right = new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld, 0);
                const up = new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld, 1);
                const projectedX = direction.dot(right);
                const projectedY = direction.dot(up);
                if (projectedX * projectedX + projectedY * projectedY > 1e-10) {
                    this.bendHandle.rotateZ(Math.atan2(projectedY, projectedX));
                }
            }
        }
        for (const [name, handle] of this.trunkHandles) {
            if (!handle.visible) {
                continue;
            }
            let worldPerPixel;
            if (camera.isPerspectiveCamera) {
                const distance = camera.position.distanceTo(handle.position);
                worldPerPixel = 2 * distance *
                    Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / safeHeight;
            } else {
                worldPerPixel = (camera.top - camera.bottom) / (camera.zoom * safeHeight);
            }
            const emphasis = handle.userData.poseEmphasized ? 1.22 : 1;
            handle.scale.setScalar(THREE.MathUtils.clamp(
                worldPerPixel * TRUNK_HANDLE_RADIUS_PIXELS * emphasis,
                0.012,
                0.2
            ));
            handle.quaternion.copy(camera.quaternion);
            this.trunkHitTargets.get(name).scale.setScalar(THREE.MathUtils.clamp(
                worldPerPixel * TRUNK_HANDLE_HIT_RADIUS_PIXELS,
                0.018,
                0.28
            ));
        }
        for (const [boneName, marker] of this.bodyMarkers) {
            if (!marker.visible) {
                continue;
            }
            let worldPerPixel;
            if (camera.isPerspectiveCamera) {
                const distance = camera.position.distanceTo(marker.position);
                worldPerPixel = 2 * distance *
                    Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / safeHeight;
            } else {
                worldPerPixel = (camera.top - camera.bottom) / (camera.zoom * safeHeight);
            }
            const emphasis = marker.userData.poseEmphasized ? 1.2 : 1;
            marker.scale.setScalar(THREE.MathUtils.clamp(
                worldPerPixel * BODY_MARKER_RADIUS_PIXELS * emphasis,
                0.006,
                0.09
            ));
            this.bodyMarkerHitTargets.get(boneName).scale.setScalar(THREE.MathUtils.clamp(
                worldPerPixel * BODY_MARKER_HIT_RADIUS_PIXELS,
                0.012,
                0.18
            ));
        }
        for (const [boneName, group] of this.rotationHandles) {
            if (!group.visible) {
                continue;
            }
            let worldPerPixel;
            if (camera.isPerspectiveCamera) {
                const distance = camera.position.distanceTo(group.position);
                worldPerPixel = 2 * distance *
                    Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / safeHeight;
            } else {
                worldPerPixel = (camera.top - camera.bottom) / (camera.zoom * safeHeight);
            }
            const emphasis = group.userData.poseEmphasized ? 1.12 : 1;
            const sizeMultiplier = ROTATION_TARGETS[boneName].sizeMultiplier ?? 1;
            group.scale.setScalar(sizeMultiplier * THREE.MathUtils.clamp(
                worldPerPixel * ROTATION_HANDLE_RADIUS_PIXELS * emphasis,
                0.018,
                0.28
            ));
        }
    }
}

export {
    deltaToLocal,
    EULER_ORDER,
    eulerDegreesToQuaternion,
    LIMBS,
    neutralToDelta,
    PoseController,
    quaternionToEulerDegrees
};
