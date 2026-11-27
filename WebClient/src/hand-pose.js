import * as THREE from 'three';

export const HAND_SIDES = Object.freeze(['left', 'right']);
export const FINGER_NAMES = Object.freeze(['thumb', 'index', 'middle', 'ring', 'little']);
export const SPREAD_FINGER_NAMES = Object.freeze(['index', 'middle', 'ring', 'little']);

const JOINT_SUFFIXES = Object.freeze({
    thumb: ['ThumbMetacarpal', 'ThumbProximal', 'ThumbDistal'],
    index: ['IndexProximal', 'IndexIntermediate', 'IndexDistal'],
    middle: ['MiddleProximal', 'MiddleIntermediate', 'MiddleDistal'],
    ring: ['RingProximal', 'RingIntermediate', 'RingDistal'],
    little: ['LittleProximal', 'LittleIntermediate', 'LittleDistal']
});

const MAXIMUM_CURL_DEGREES = Object.freeze({
    index: [64, 82, 68],
    middle: [68, 86, 70],
    ring: [70, 88, 72],
    little: [72, 90, 74]
});

const MAXIMUM_SPREAD_DEGREES = Object.freeze({
    index: 12,
    middle: 2,
    ring: 7,
    little: 15
});

const EPSILON = 1e-10;

function capitalize(value) {
    return `${value[0].toUpperCase()}${value.slice(1)}`;
}

export function fingerBoneNames(side, finger) {
    const suffixes = JOINT_SUFFIXES[finger];
    if (!HAND_SIDES.includes(side) || !suffixes) {
        return [];
    }
    return suffixes.map((suffix) => `${side}${suffix}`);
}

export function resolveFingerChains(bones) {
    const hands = {};
    for (const side of HAND_SIDES) {
        const fingers = {};
        for (const finger of FINGER_NAMES) {
            const joints = fingerBoneNames(side, finger)
                .map((name, index) => ({
                    name,
                    index,
                    node: bones.get(name) ?? null
                }))
                .filter((joint) => joint.node);
            fingers[finger] = joints;
        }
        hands[side] = {
            handName: `${side}Hand`,
            hand: bones.get(`${side}Hand`) ?? null,
            fingers,
            available: Object.values(fingers).some((joints) => joints.length > 0)
        };
    }
    return hands;
}

function finiteUnitVector(value, fallback) {
    if (value?.isVector3 &&
        value.toArray().every(Number.isFinite) &&
        value.lengthSq() > EPSILON) {
        return value.clone().normalize();
    }
    return fallback.clone().normalize();
}

function projectedUnitVector(value, normal, fallback) {
    const projected = value.clone().addScaledVector(normal, -value.dot(normal));
    return finiteUnitVector(projected, fallback);
}

function worldPosition(node) {
    node?.updateWorldMatrix(true, false);
    return node?.getWorldPosition(new THREE.Vector3()) ?? null;
}

function jointDirection(joints, index, fallback) {
    const origin = worldPosition(joints[index]?.node);
    const next = worldPosition(joints[index + 1]?.node);
    if (origin && next) {
        return finiteUnitVector(next.sub(origin), fallback);
    }
    if (index > 0) {
        const previous = worldPosition(joints[index - 1]?.node);
        if (origin && previous) {
            return finiteUnitVector(origin.sub(previous), fallback);
        }
    }
    return fallback.clone();
}

function averageDirection(vectors, fallback) {
    const sum = new THREE.Vector3();
    for (const vector of vectors) {
        if (vector?.isVector3 && vector.lengthSq() > EPSILON) {
            sum.add(vector);
        }
    }
    return finiteUnitVector(sum, fallback);
}

function proximalJoint(handRig, finger) {
    return handRig?.fingers?.[finger]?.find((joint) => joint.index === 0) ?? null;
}

function avatarUpDirection(bones) {
    const hips = worldPosition(bones.get('hips'));
    const upper = worldPosition(
        bones.get('head') ??
        bones.get('neck') ??
        bones.get('upperChest') ??
        bones.get('chest') ??
        bones.get('spine')
    );
    if (hips && upper) {
        return finiteUnitVector(upper.sub(hips), new THREE.Vector3(0, 1, 0));
    }
    return new THREE.Vector3(0, 1, 0);
}

export function localAxisFromWorld(node, axisWorld, fallbackWorld) {
    const inverseWorldQuaternion = node
        .getWorldQuaternion(new THREE.Quaternion())
        .invert();
    return finiteUnitVector(
        axisWorld.clone().applyQuaternion(inverseWorldQuaternion),
        fallbackWorld.clone().applyQuaternion(inverseWorldQuaternion)
    );
}

/**
 * Builds a stable palm coordinate frame from normalized humanoid neutral bone
 * positions. three-vrm's normalized rig retains the source world positions but
 * gives the normalized bones identity rest rotations, so no model-local Euler
 * axis is assumed here.
 */
function buildPalmFrame(bones, handRig, side) {
    const sideFallback = new THREE.Vector3(side === 'left' ? -1 : 1, 0, 0);
    const fingerDirections = SPREAD_FINGER_NAMES.map((finger) => {
        const joints = handRig.fingers[finger];
        return joints.length > 1 ? jointDirection(joints, 0, sideFallback) : null;
    }).filter(Boolean);
    const forward = averageDirection(fingerDirections, sideFallback);

    const indexRoot = worldPosition(proximalJoint(handRig, 'index')?.node);
    const littleRoot = worldPosition(proximalJoint(handRig, 'little')?.node);
    const acrossCandidate = indexRoot && littleRoot
        ? littleRoot.sub(indexRoot)
        : new THREE.Vector3(0, 0, 1);
    let across = projectedUnitVector(
        acrossCandidate,
        forward,
        Math.abs(forward.y) < 0.8
            ? new THREE.Vector3(0, 1, 0).cross(forward)
            : new THREE.Vector3(0, 0, 1).cross(forward)
    );

    const avatarUp = avatarUpDirection(bones);
    let palmNormal = finiteUnitVector(
        forward.clone().cross(across),
        avatarUp.clone().negate()
    );
    if (palmNormal.dot(avatarUp) > 0) {
        palmNormal.negate();
        across.negate();
    }

    const roots = SPREAD_FINGER_NAMES
        .map((finger) => worldPosition(proximalJoint(handRig, finger)?.node))
        .filter(Boolean);
    const center = roots.length
        ? roots.reduce((sum, position) => sum.add(position), new THREE.Vector3())
            .multiplyScalar(1 / roots.length)
        : worldPosition(handRig.hand) ?? new THREE.Vector3();

    return { forward, across, palmNormal, center };
}

function configureCurlJoint(joint, neutralRotations, axisWorld, fallbackWorld, finger) {
    joint.axis = localAxisFromWorld(joint.node, axisWorld, fallbackWorld);
    joint.neutral = neutralRotations.get(joint.name)?.clone().normalize() ??
        joint.node.quaternion.clone().normalize();
    joint.maximumRadians = THREE.MathUtils.degToRad(
        MAXIMUM_CURL_DEGREES[finger][joint.index] ??
        MAXIMUM_CURL_DEGREES[finger].at(-1)
    );
}

function localDeltaFromWorldDirections(node, fromWorld, targetWorld, maximumDegrees) {
    const from = finiteUnitVector(fromWorld, new THREE.Vector3(1, 0, 0));
    const target = finiteUnitVector(targetWorld, from);
    const worldDelta = new THREE.Quaternion().setFromUnitVectors(from, target);
    const maximumRadians = THREE.MathUtils.degToRad(maximumDegrees);
    const angle = worldDelta.angleTo(identityQuaternion());
    if (angle > maximumRadians && angle > EPSILON) {
        worldDelta.slerp(identityQuaternion(), 1 - maximumRadians / angle);
    }
    const worldQuaternion = node.getWorldQuaternion(new THREE.Quaternion());
    return worldQuaternion.clone()
        .invert()
        .multiply(worldDelta)
        .multiply(worldQuaternion)
        .normalize();
}

function localFlexionDelta(node, directionWorld, bendDirectionWorld, maximumDegrees) {
    const direction = finiteUnitVector(directionWorld, new THREE.Vector3(1, 0, 0));
    const fallbackBend = projectedUnitVector(
        Math.abs(direction.y) < 0.8
            ? new THREE.Vector3(0, 1, 0)
            : new THREE.Vector3(0, 0, 1),
        direction,
        new THREE.Vector3(0, 0, 1)
    );
    const bendDirection = projectedUnitVector(
        bendDirectionWorld,
        direction,
        fallbackBend
    );
    const flexionAxisWorld = finiteUnitVector(
        direction.clone().cross(bendDirection),
        direction.clone().cross(fallbackBend)
    );
    const flexionAxisLocal = localAxisFromWorld(
        node,
        flexionAxisWorld,
        flexionAxisWorld
    );
    return {
        axisLocal: flexionAxisLocal,
        bendDirectionWorld: bendDirection,
        delta: new THREE.Quaternion().setFromAxisAngle(
            flexionAxisLocal,
            THREE.MathUtils.degToRad(maximumDegrees)
        )
    };
}

function configureThumbCurl(handRig, frame, neutralRotations) {
    const joints = handRig.fingers.thumb;
    if (!joints.length) {
        return;
    }
    const indexPosition = worldPosition(proximalJoint(handRig, 'index')?.node);
    const middlePosition = worldPosition(proximalJoint(handRig, 'middle')?.node);
    const fingerAnchor = indexPosition && middlePosition
        ? indexPosition.clone().lerp(middlePosition, 0.35)
        : indexPosition ?? middlePosition ?? frame.center.clone();
    const oppositionDegrees = [46, 16, 0];
    const flexionDegrees = [10, 34, 52];

    for (let offset = 0; offset < joints.length; offset += 1) {
        const joint = joints[offset];
        const position = worldPosition(joint.node) ?? frame.center.clone();
        const direction = jointDirection(joints, offset, frame.forward);
        const oppositionTarget = projectedUnitVector(
            fingerAnchor.clone().sub(position),
            frame.palmNormal,
            projectedUnitVector(frame.across, frame.palmNormal, frame.forward)
        );
        joint.neutral = neutralRotations.get(joint.name)?.clone().normalize() ??
            joint.node.quaternion.clone().normalize();
        joint.thumbOppositionDelta = localDeltaFromWorldDirections(
            joint.node,
            direction,
            oppositionTarget,
            oppositionDegrees[joint.index] ?? oppositionDegrees.at(-1)
        );
        const inwardBendDirection = projectedUnitVector(
            frame.center.clone().sub(position),
            direction,
            frame.palmNormal
        );
        const flexion = localFlexionDelta(
            joint.node,
            direction,
            joint.index === 2 ? inwardBendDirection : frame.palmNormal,
            flexionDegrees[joint.index] ?? flexionDegrees.at(-1)
        );
        joint.thumbFlexionAxis = flexion.axisLocal;
        joint.thumbFlexionDirection = flexion.bendDirectionWorld;
        joint.thumbFlexionDelta = flexion.delta;
        // Opposition carries the thumb across the palm. Flexion is a local
        // hinge attached to each joint, so parent opposition cannot flatten
        // the Proximal or Distal bend in the final hierarchical pose.
        joint.maximumThumbDelta = joint.thumbOppositionDelta.clone()
            .multiply(joint.thumbFlexionDelta)
            .normalize();
    }
}

function configureFingerCurl(handRig, finger, frame, neutralRotations) {
    const joints = handRig.fingers[finger];
    if (!joints.length) {
        return;
    }
    if (finger === 'thumb') {
        configureThumbCurl(handRig, frame, neutralRotations);
        return;
    }
    const direction = jointDirection(joints, 0, frame.forward);
    const curlTarget = frame.palmNormal;
    const axisWorld = finiteUnitVector(
        direction.clone().cross(curlTarget),
        projectedUnitVector(frame.across, direction, frame.palmNormal)
    );
    for (const joint of joints) {
        configureCurlJoint(
            joint,
            neutralRotations,
            axisWorld,
            projectedUnitVector(frame.across, direction, frame.palmNormal),
            finger
        );
    }
}

function configureSpread(handRig, frame) {
    for (const finger of SPREAD_FINGER_NAMES) {
        const proximal = proximalJoint(handRig, finger);
        if (!proximal) {
            continue;
        }
        const origin = worldPosition(proximal.node) ?? frame.center.clone();
        const outward = origin.clone().sub(frame.center);
        const direction = jointDirection(handRig.fingers[finger], 0, frame.forward);
        const positiveMotion = frame.palmNormal.clone().cross(direction);
        let sign = Math.sign(positiveMotion.dot(outward));
        if (sign === 0) {
            sign = finger === 'index' ? -1 : finger === 'middle' ? -1 : 1;
        }
        proximal.spreadAxis = localAxisFromWorld(
            proximal.node,
            frame.palmNormal,
            frame.palmNormal
        );
        proximal.maximumSpreadRadians =
            THREE.MathUtils.degToRad(MAXIMUM_SPREAD_DEGREES[finger]) * sign;
    }
}

export function buildFingerCurlRig(bones, neutralRotations) {
    const resolved = resolveFingerChains(bones);
    for (const side of HAND_SIDES) {
        const handRig = resolved[side];
        handRig.hand?.updateWorldMatrix(true, true);
        if (!handRig.hand || !handRig.available) {
            continue;
        }
        const frame = buildPalmFrame(bones, handRig, side);
        handRig.palmFrame = frame;
        for (const finger of FINGER_NAMES) {
            configureFingerCurl(handRig, finger, frame, neutralRotations);
        }
        configureSpread(handRig, frame);
    }
    return resolved;
}

function finiteAmount(value, label = 'Finger pose') {
    const amount = Number(value);
    if (!Number.isFinite(amount)) {
        throw new TypeError(`${label} must be finite.`);
    }
    return THREE.MathUtils.clamp(amount, 0, 1);
}

function signedTwistAngle(quaternion, axis) {
    const normalized = quaternion.clone().normalize();
    const projection = normalized.x * axis.x +
        normalized.y * axis.y +
        normalized.z * axis.z;
    return 2 * Math.atan2(projection, normalized.w);
}

function rotationEstimate(joint, axis, maximumRadians) {
    if (!joint?.neutral || !axis || !(Math.abs(maximumRadians) > 1e-6)) {
        return null;
    }
    const delta = joint.neutral.clone().invert().multiply(joint.node.quaternion).normalize();
    const estimate = signedTwistAngle(delta, axis) / maximumRadians;
    return Number.isFinite(estimate) ? estimate : null;
}

function estimateThumbJointCurl(joint) {
    if (!joint?.neutral || !joint.maximumThumbDelta) {
        return null;
    }
    const maximumAngle = identityQuaternion().angleTo(joint.maximumThumbDelta);
    if (!(maximumAngle > 1e-6)) {
        return null;
    }
    const maximumAxis = new THREE.Vector3(
        joint.maximumThumbDelta.x,
        joint.maximumThumbDelta.y,
        joint.maximumThumbDelta.z
    );
    if (maximumAxis.lengthSq() <= EPSILON) {
        return null;
    }
    maximumAxis.normalize();
    const delta = joint.neutral.clone().invert().multiply(joint.node.quaternion).normalize();
    const estimate = signedTwistAngle(delta, maximumAxis) / maximumAngle;
    return Number.isFinite(estimate) ? estimate : null;
}

export function estimateFingerCurl(joints) {
    if (!joints?.length) {
        return 0;
    }
    // Spread affects only Proximal. Prefer the unaffected downstream joints so
    // a spread edit never moves a vertical curl slider.
    const downstream = joints.filter((joint) => joint.index > 0);
    const candidates = downstream.length ? downstream : joints;
    const estimates = candidates
        .map((joint) => joint.maximumThumbDelta
            ? estimateThumbJointCurl(joint)
            : rotationEstimate(joint, joint.axis, joint.maximumRadians))
        .filter(Number.isFinite);
    if (estimates.length === 0) {
        return 0;
    }
    return THREE.MathUtils.clamp(
        estimates.reduce((sum, value) => sum + value, 0) / estimates.length,
        0,
        1
    );
}

export function estimateFingerSpread(handRig) {
    const estimates = SPREAD_FINGER_NAMES
        .map((finger) => proximalJoint(handRig, finger))
        .map((joint) => rotationEstimate(
            joint,
            joint?.spreadAxis,
            joint?.maximumSpreadRadians
        ))
        .filter(Number.isFinite);
    if (estimates.length === 0) {
        return 0;
    }
    return THREE.MathUtils.clamp(
        estimates.reduce((sum, value) => sum + value, 0) / estimates.length,
        0,
        1
    );
}

function identityQuaternion() {
    return new THREE.Quaternion();
}

function fingerComponentQuaternion(joint, finger, state) {
    const component = identityQuaternion();
    if (joint.index === 0 &&
        joint.spreadAxis &&
        Number.isFinite(joint.maximumSpreadRadians)) {
        component.multiply(new THREE.Quaternion().setFromAxisAngle(
            joint.spreadAxis,
            state.fingerSpread * joint.maximumSpreadRadians
        ));
    }
    if (joint.axis && Number.isFinite(joint.maximumRadians)) {
        component.multiply(new THREE.Quaternion().setFromAxisAngle(
            joint.axis,
            state.curls[finger] * joint.maximumRadians
        ));
    } else if (joint.maximumThumbDelta) {
        component.multiply(identityQuaternion().slerp(
            joint.maximumThumbDelta,
            state.curls[finger]
        ));
    }
    return component.normalize();
}

function findFingerJoint(handRig, boneName) {
    for (const finger of FINGER_NAMES) {
        const joint = handRig?.fingers?.[finger]?.find((item) => item.name === boneName);
        if (joint) {
            return { finger, joint };
        }
    }
    return null;
}

function composeJoint(joint, finger, state) {
    const manual = state.manualRotations.get(joint.name) ?? joint.neutral;
    if (!manual) {
        return false;
    }
    const rotation = manual.clone()
        .multiply(fingerComponentQuaternion(joint, finger, state))
        .normalize();
    if (!rotation.toArray().every(Number.isFinite)) {
        throw new TypeError('Finger pose produced a non-finite rotation.');
    }
    joint.node.quaternion.copy(rotation);
    return true;
}

export function createFingerPoseStates(rig) {
    const states = {};
    for (const side of HAND_SIDES) {
        const state = {
            curls: Object.fromEntries(FINGER_NAMES.map((finger) => [finger, 0])),
            fingerSpread: 0,
            manualRotations: new Map()
        };
        for (const finger of FINGER_NAMES) {
            for (const joint of rig?.[side]?.fingers?.[finger] ?? []) {
                state.manualRotations.set(
                    joint.name,
                    joint.node.quaternion.clone().normalize()
                );
            }
        }
        states[side] = state;
    }
    return states;
}

export function applyFingerCurlState(handRig, state, finger, amount) {
    const joints = handRig?.fingers?.[finger] ?? [];
    if (!state || !joints.length) {
        return false;
    }
    const targetAmount = finiteAmount(amount, 'Finger curl');
    const previousAmount = state.curls[finger];
    if (Math.abs(targetAmount - previousAmount) <= Number.EPSILON) {
        return false;
    }
    const previous = joints.map((joint) => joint.node.quaternion.clone());
    try {
        state.curls[finger] = targetAmount;
        for (const joint of joints) {
            composeJoint(joint, finger, state);
        }
        return true;
    } catch (error) {
        state.curls[finger] = previousAmount;
        joints.forEach((joint, index) => joint.node.quaternion.copy(previous[index]));
        throw error;
    }
}

export function applyFingerSpreadState(handRig, state, amount) {
    if (!state) {
        return false;
    }
    const joints = SPREAD_FINGER_NAMES
        .map((finger) => ({ finger, joint: proximalJoint(handRig, finger) }))
        .filter(({ joint }) => Boolean(joint));
    if (!joints.length) {
        return false;
    }
    const targetAmount = finiteAmount(amount, 'Finger spread');
    const previousAmount = state.fingerSpread;
    if (Math.abs(targetAmount - previousAmount) <= Number.EPSILON) {
        return false;
    }
    const previous = joints.map(({ joint }) => joint.node.quaternion.clone());
    try {
        state.fingerSpread = targetAmount;
        for (const { finger, joint } of joints) {
            composeJoint(joint, finger, state);
        }
        return true;
    } catch (error) {
        state.fingerSpread = previousAmount;
        joints.forEach(({ joint }, index) => joint.node.quaternion.copy(previous[index]));
        throw error;
    }
}

export function rebaseFingerJoint(handRig, state, boneName) {
    const resolved = findFingerJoint(handRig, boneName);
    if (!state || !resolved) {
        return false;
    }
    const { finger, joint } = resolved;
    const component = fingerComponentQuaternion(joint, finger, state);
    const manual = joint.node.quaternion.clone()
        .multiply(component.invert())
        .normalize();
    if (!manual.toArray().every(Number.isFinite)) {
        throw new TypeError('Finger manual rotation must be finite.');
    }
    state.manualRotations.set(joint.name, manual);
    return true;
}

/**
 * Explicit synchronization point for imported poses. Semantic amounts are
 * estimated once, then the manual base is solved so recomposition preserves
 * every imported local quaternion exactly.
 */
export function synchronizeFingerPoseState(handRig, state) {
    if (!state) {
        return false;
    }
    for (const finger of FINGER_NAMES) {
        state.curls[finger] = estimateFingerCurl(handRig?.fingers?.[finger] ?? []);
    }
    state.fingerSpread = estimateFingerSpread(handRig);
    for (const finger of FINGER_NAMES) {
        for (const joint of handRig?.fingers?.[finger] ?? []) {
            rebaseFingerJoint(handRig, state, joint.name);
        }
    }
    return true;
}

export function resetFingerPoseState(handRig, state) {
    if (!state) {
        return false;
    }
    let changed = false;
    state.fingerSpread = 0;
    for (const finger of FINGER_NAMES) {
        state.curls[finger] = 0;
        for (const joint of handRig?.fingers?.[finger] ?? []) {
            joint.node.quaternion.copy(joint.neutral);
            state.manualRotations.set(joint.name, joint.neutral.clone());
            changed = true;
        }
    }
    return changed;
}

export function handPoseState(rig, semanticStates = null) {
    const result = {};
    for (const side of HAND_SIDES) {
        const semantic = semanticStates?.[side] ?? null;
        const fingers = {};
        for (const finger of FINGER_NAMES) {
            const joints = rig?.[side]?.fingers?.[finger] ?? [];
            fingers[finger] = {
                available: joints.length > 0,
                value: semantic
                    ? finiteAmount(semantic.curls[finger], 'Finger curl')
                    : estimateFingerCurl(joints),
                bone: joints[0]?.name ?? ''
            };
        }
        result[side] = {
            available: Boolean(rig?.[side]?.available),
            fingers,
            fingerSpreadAvailable: SPREAD_FINGER_NAMES.some(
                (finger) => Boolean(proximalJoint(rig?.[side], finger))
            ),
            fingerSpread: semantic
                ? finiteAmount(semantic.fingerSpread, 'Finger spread')
                : estimateFingerSpread(rig?.[side])
        };
    }
    return result;
}

export function fingerDisplayName(finger) {
    return capitalize(finger);
}
