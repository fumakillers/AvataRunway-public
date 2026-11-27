import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';
import {
    FINGER_NAMES,
    SPREAD_FINGER_NAMES,
    applyFingerCurlState,
    applyFingerSpreadState,
    buildFingerCurlRig,
    createFingerPoseStates,
    estimateFingerCurl,
    estimateFingerSpread,
    fingerBoneNames,
    handPoseState,
    localAxisFromWorld,
    rebaseFingerJoint,
    resolveFingerChains
} from '../src/hand-pose.js';

const FINGER_Z = Object.freeze({
    index: -0.035,
    middle: -0.012,
    ring: 0.012,
    little: 0.038
});

function createJoint(parent, name, position, bones, neutral) {
    const joint = new THREE.Bone();
    joint.name = name;
    joint.position.copy(position);
    parent.add(joint);
    bones.set(name, joint);
    neutral.set(name, joint.quaternion.clone());
    return joint;
}

function makeHumanoid({
    sides = ['left', 'right'],
    missing = new Set(),
    ancestorRotation = new THREE.Euler(),
    thumbLayout = 'vrm1'
} = {}) {
    const bones = new Map();
    const neutral = new Map();
    const tips = new Map();
    const root = new THREE.Object3D();
    root.quaternion.setFromEuler(ancestorRotation);

    const hips = createJoint(root, 'hips', new THREE.Vector3(0, 0.9, 0), bones, neutral);
    createJoint(hips, 'head', new THREE.Vector3(0, 0.75, 0), bones, neutral);

    for (const side of sides) {
        const direction = side === 'left' ? -1 : 1;
        const hand = createJoint(
            root,
            `${side}Hand`,
            new THREE.Vector3(direction * 0.55, 1.25, 0),
            bones,
            neutral
        );

        for (const finger of FINGER_NAMES) {
            const names = fingerBoneNames(side, finger);
            let parent = hand;
            for (const [index, name] of names.entries()) {
                if (missing.has(name)) {
                    continue;
                }
                const isThumb = finger === 'thumb';
                const position = isThumb
                    ? (thumbLayout === 'vrm0'
                        ? [
                            new THREE.Vector3(direction * 0.03, -0.018, -0.06),
                            new THREE.Vector3(direction * 0.045, 0.004, -0.035),
                            new THREE.Vector3(direction * 0.05, -0.004, -0.018)
                        ][index]
                        : [
                            new THREE.Vector3(direction * 0.035, -0.012, -0.055),
                            new THREE.Vector3(direction * 0.055, -0.008, -0.025),
                            new THREE.Vector3(direction * 0.055, -0.008, -0.025)
                        ][index])
                    : index === 0
                        ? new THREE.Vector3(direction * 0.075, 0, FINGER_Z[finger])
                        : new THREE.Vector3(direction * (index === 1 ? 0.085 : 0.065), 0, 0);
                parent = createJoint(parent, name, position, bones, neutral);
            }
            if (parent !== hand) {
                const tip = new THREE.Object3D();
                tip.position.set(
                    direction * (finger === 'thumb'
                        ? thumbLayout === 'vrm0' ? 0.035 : 0.04
                        : 0.05),
                    finger === 'thumb'
                        ? thumbLayout === 'vrm0' ? -0.004 : -0.006
                        : 0,
                    finger === 'thumb'
                        ? thumbLayout === 'vrm0' ? -0.014 : -0.018
                        : 0
                );
                parent.add(tip);
                tips.set(`${side}:${finger}`, tip);
            }
        }
    }
    root.updateWorldMatrix(true, true);
    return { bones, neutral, root, tips };
}

function localToRoot(root, worldPoint) {
    return root.worldToLocal(worldPoint.clone());
}

function tipPosition(fixture, side, finger) {
    fixture.root.updateWorldMatrix(true, true);
    return localToRoot(
        fixture.root,
        fixture.tips.get(`${side}:${finger}`).getWorldPosition(new THREE.Vector3())
    );
}

function signedAngleAroundAxis(from, to, axis) {
    const normal = axis.clone().normalize();
    const projectedFrom = from.clone()
        .addScaledVector(normal, -from.dot(normal))
        .normalize();
    const projectedTo = to.clone()
        .addScaledVector(normal, -to.dot(normal))
        .normalize();
    return Math.atan2(
        normal.dot(projectedFrom.clone().cross(projectedTo)),
        THREE.MathUtils.clamp(projectedFrom.dot(projectedTo), -1, 1)
    );
}

function thumbDistalMeasurement(fixture, rig, side) {
    fixture.root.updateWorldMatrix(true, true);
    const proximal = fixture.bones.get(`${side}ThumbProximal`)
        ?.getWorldPosition(new THREE.Vector3());
    const distal = fixture.bones.get(`${side}ThumbDistal`)
        ?.getWorldPosition(new THREE.Vector3());
    const tip = fixture.tips.get(`${side}:thumb`)
        ?.getWorldPosition(new THREE.Vector3());
    const distalJoint = rig?.[side]?.fingers?.thumb
        ?.find((joint) => joint.index === 2);
    if (!proximal || !distal || !tip || !distalJoint?.thumbFlexionAxis) {
        return null;
    }
    const hingeAxis = distalJoint.thumbFlexionAxis.clone().applyQuaternion(
        distalJoint.node.getWorldQuaternion(new THREE.Quaternion())
    );
    const proximalToDistal = distal.clone().sub(proximal).normalize();
    const distalToTip = tip.clone().sub(distal).normalize();
    return {
        proximalToDistal,
        distalToTip,
        hingeAxis,
        signedAngle: signedAngleAroundAxis(
            proximalToDistal,
            distalToTip,
            hingeAxis
        )
    };
}

function thumbDistalSignedAngle(fixture, rig, side) {
    return thumbDistalMeasurement(fixture, rig, side)?.signedAngle ?? null;
}

function formatThumbMeasurement(measurement) {
    const vector = (value) => value.toArray().map((component) => Number(component.toFixed(4)));
    return JSON.stringify({
        proximalToDistal: vector(measurement.proximalToDistal),
        distalToTip: vector(measurement.distalToTip),
        hingeAxis: vector(measurement.hingeAxis),
        signedDegrees: Number(THREE.MathUtils.radToDeg(measurement.signedAngle).toFixed(2))
    });
}

function quaternionSnapshot(rig, except = new Set()) {
    const result = new Map();
    for (const side of ['left', 'right']) {
        for (const finger of FINGER_NAMES) {
            for (const joint of rig[side].fingers[finger]) {
                if (!except.has(joint.name)) {
                    result.set(joint.name, joint.node.quaternion.clone());
                }
            }
        }
    }
    return result;
}

function assertSnapshotUnchanged(snapshot, bones, tolerance = 1e-9) {
    for (const [name, expected] of snapshot) {
        assert.ok(
            bones.get(name).quaternion.angleTo(expected) < tolerance,
            `${name} changed unexpectedly`
        );
    }
}

function assertNear(actual, expected, tolerance, message) {
    assert.ok(
        Math.abs(actual - expected) <= tolerance,
        `${message}: expected ${expected}, got ${actual}`
    );
}

test('normalized thumb names resolve for the common VRM 0.x and VRM 1.0 mapping', () => {
    assert.deepEqual(fingerBoneNames('left', 'thumb'), [
        'leftThumbMetacarpal',
        'leftThumbProximal',
        'leftThumbDistal'
    ]);
    const fixture = makeHumanoid();
    const resolved = resolveFingerChains(fixture.bones);
    assert.equal(resolved.left.fingers.thumb.length, 3);
    assert.equal(resolved.right.fingers.thumb.length, 3);
});

test('resolver keeps available joints when a finger is incomplete', () => {
    const missing = new Set(['leftIndexIntermediate', 'leftIndexDistal']);
    const fixture = makeHumanoid({ sides: ['left'], missing });
    const resolved = resolveFingerChains(fixture.bones);
    assert.equal(resolved.left.fingers.index.length, 1);
    assert.equal(resolved.left.fingers.middle.length, 3);
    assert.equal(resolved.right.available, false);
});

test('index curl moves only its fingertip toward the palm in one bend plane', () => {
    const fixture = makeHumanoid({ sides: ['left'] });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const joints = rig.left.fingers.index;
    const affected = new Set(joints.map((joint) => joint.name));
    const untouched = quaternionSnapshot(rig, affected);
    const before = tipPosition(fixture, 'left', 'index');
    const rootZ = localToRoot(
        fixture.root,
        joints[0].node.getWorldPosition(new THREE.Vector3())
    ).z;

    assert.equal(applyFingerCurlState(rig.left, states.left, 'index', 0.8), true);
    const after = tipPosition(fixture, 'left', 'index');
    assert.ok(after.y < before.y - 0.04, 'fingertip did not move toward the palm');
    assertNear(after.z, rootZ, 1e-6, 'fingertip left the common bend plane');
    assertSnapshotUnchanged(untouched, fixture.bones);

    for (const joint of joints) {
        const direction = new THREE.Vector3(-1, 0, 0);
        const delta = joint.neutral.clone().invert().multiply(joint.node.quaternion);
        const twistProjection = Math.abs(
            delta.x * direction.x + delta.y * direction.y + delta.z * direction.z
        );
        assert.ok(twistProjection < 1e-8, `${joint.name} contains longitudinal twist`);
    }
    assertNear(estimateFingerCurl(joints), 0.8, 1e-6, 'curl estimate');
});

test('left and right curls are mirrored and remain semantic under a rotated ancestor', () => {
    const fixture = makeHumanoid({
        ancestorRotation: new THREE.Euler(0.31, -0.48, 0.22)
    });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const leftBefore = tipPosition(fixture, 'left', 'middle');
    const rightBefore = tipPosition(fixture, 'right', 'middle');
    applyFingerCurlState(rig.left, states.left, 'middle', 0.65);
    applyFingerCurlState(rig.right, states.right, 'middle', 0.65);
    const leftAfter = tipPosition(fixture, 'left', 'middle');
    const rightAfter = tipPosition(fixture, 'right', 'middle');

    assertNear(leftAfter.y - leftBefore.y, rightAfter.y - rightBefore.y, 1e-6, 'palm displacement');
    assertNear(leftAfter.x, -rightAfter.x, 1e-6, 'mirrored longitudinal position');
    assertNear(leftAfter.z, rightAfter.z, 1e-6, 'mirrored bend plane');
});

test('VRM 1.0-like thumb maximum pose opposes and visibly flexes the distal joint', (t) => {
    const fixture = makeHumanoid({
        sides: ['right'],
        ancestorRotation: new THREE.Euler(0.27, -0.34, 0.19)
    });
    for (const [index, name] of fingerBoneNames('right', 'thumb').entries()) {
        const rotation = new THREE.Quaternion().setFromEuler(
            new THREE.Euler(0.025 * (index + 1), -0.018 * (index + 1), 0.014)
        );
        fixture.bones.get(name).quaternion.copy(rotation);
        fixture.neutral.set(name, rotation.clone());
    }
    fixture.root.updateWorldMatrix(true, true);
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const thumb = rig.right.fingers.thumb;
    const untouched = quaternionSnapshot(
        rig,
        new Set(thumb.map((joint) => joint.name))
    );
    const before = tipPosition(fixture, 'right', 'thumb');
    const base = localToRoot(
        fixture.root,
        thumb[0].node.getWorldPosition(new THREE.Vector3())
    );
    const center = localToRoot(fixture.root, rig.right.palmFrame.center);
    const indexPosition = localToRoot(
        fixture.root,
        rig.right.fingers.index[0].node.getWorldPosition(new THREE.Vector3())
    );
    const beforeCenterDistance = before.distanceTo(center);
    const beforeIndexDistance = before.distanceTo(indexPosition);
    const beforeAlignment = before.clone().sub(base).normalize()
        .dot(center.clone().sub(base).normalize());
    const beforeMeasurement = thumbDistalMeasurement(fixture, rig, 'right');
    const beforeDistalAngle = beforeMeasurement.signedAngle;

    applyFingerCurlState(rig.right, states.right, 'thumb', 0.99);
    const nearMaximumDistal = thumb[2].node.quaternion.clone();
    const nearMaximumAngle = thumbDistalSignedAngle(fixture, rig, 'right');
    applyFingerCurlState(rig.right, states.right, 'thumb', 1);
    const after = tipPosition(fixture, 'right', 'thumb');
    const afterDistalAngle = thumbDistalSignedAngle(fixture, rig, 'right');
    const afterMeasurement = thumbDistalMeasurement(fixture, rig, 'right');
    t.diagnostic(`neutral distal: ${formatThumbMeasurement(beforeMeasurement)}`);
    t.diagnostic(`maximum distal: ${formatThumbMeasurement(afterMeasurement)}`);
    const afterAlignment = after.clone().sub(base).normalize()
        .dot(center.clone().sub(base).normalize());
    assert.ok(
        thumb[0].node.quaternion.angleTo(thumb[0].neutral) > 0.2,
        'metacarpal did not oppose into the palm'
    );
    assert.ok(afterAlignment > beforeAlignment + 0.1);
    assert.ok(after.distanceTo(center) < beforeCenterDistance);
    assert.ok(after.distanceTo(indexPosition) < beforeIndexDistance);
    assert.ok(
        afterDistalAngle > beforeDistalAngle + THREE.MathUtils.degToRad(40),
        'thumb distal joint did not gain a visible flexion angle'
    );
    assert.ok(
        afterDistalAngle > THREE.MathUtils.degToRad(45),
        'thumb distal joint remained too straight at maximum curl'
    );
    assert.ok(afterDistalAngle > nearMaximumAngle);
    assert.ok(
        thumb[2].node.quaternion.angleTo(nearMaximumDistal) <
            THREE.MathUtils.degToRad(1),
        'thumb pose jumped between 0.99 and 1.0'
    );
    assert.ok(thumb.every(
        (joint) => joint.node.quaternion.toArray().every(Number.isFinite)
    ));
    assert.ok(thumb.every(
        (joint) => joint.node.quaternion.angleTo(joint.neutral) > 0.1
    ));
    const deltas = thumb.map((joint) => joint.maximumThumbDelta);
    assert.ok(!deltas[0].equals(deltas[1]));
    assert.ok(!deltas[1].equals(deltas[2]));
    const distalTipDirection = fixture.tips.get('right:thumb').position.clone().normalize();
    assert.ok(
        distalTipDirection.angleTo(
            distalTipDirection.clone().applyQuaternion(thumb[2].thumbFlexionDelta)
        ) > THREE.MathUtils.degToRad(40),
        'thumb distal delta is only longitudinal twist'
    );

    const maximumDistal = thumb[2].node.quaternion.clone();
    thumb[2].node.quaternion.copy(thumb[2].neutral);
    const withoutDistalAngle = thumbDistalSignedAngle(fixture, rig, 'right');
    assert.ok(
        afterDistalAngle > withoutDistalAngle + THREE.MathUtils.degToRad(40),
        'metacarpal/proximal motion passed without distal flexion'
    );
    thumb[2].node.quaternion.copy(maximumDistal);

    thumb[2].node.quaternion
        .copy(thumb[2].neutral)
        .multiply(new THREE.Quaternion().setFromAxisAngle(
            thumb[2].thumbFlexionAxis,
            THREE.MathUtils.degToRad(-52)
        ));
    const reversedMeasurement = thumbDistalMeasurement(fixture, rig, 'right');
    t.diagnostic(`reversed distal: ${formatThumbMeasurement(reversedMeasurement)}`);
    assert.ok(
        reversedMeasurement.signedAngle <
            beforeDistalAngle - THREE.MathUtils.degToRad(40),
        'outward distal extension was not assigned the opposite sign'
    );
    assert.ok(
        !(reversedMeasurement.signedAngle >
            beforeDistalAngle + THREE.MathUtils.degToRad(40)),
        'outward distal extension incorrectly passed as inward flexion'
    );

    for (const activeIndex of [0, 1]) {
        thumb.forEach((joint) => joint.node.quaternion.copy(joint.neutral));
        thumb[activeIndex].node.quaternion
            .copy(thumb[activeIndex].neutral)
            .multiply(thumb[activeIndex].maximumThumbDelta);
        assertNear(
            thumbDistalSignedAngle(fixture, rig, 'right'),
            beforeDistalAngle,
            1e-6,
            `${thumb[activeIndex].name} alone changed the distal joint angle`
        );
    }
    assertSnapshotUnchanged(untouched, fixture.bones);
    assert.equal(states.right.fingerSpread, 0);
});

test('VRM 0.x-like thumb layout keeps opposition and distal flexion under rotated ancestors', (t) => {
    const fixture = makeHumanoid({
        sides: ['right'],
        thumbLayout: 'vrm0',
        ancestorRotation: new THREE.Euler(-0.29, 0.38, -0.21)
    });
    for (const [index, name] of fingerBoneNames('right', 'thumb').entries()) {
        const rotation = new THREE.Quaternion().setFromEuler(
            new THREE.Euler(-0.018 * (index + 1), 0.013 * (index + 1), -0.02)
        );
        fixture.bones.get(name).quaternion.copy(rotation);
        fixture.neutral.set(name, rotation.clone());
    }
    fixture.root.updateWorldMatrix(true, true);
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const before = tipPosition(fixture, 'right', 'thumb');
    const center = localToRoot(fixture.root, rig.right.palmFrame.center);
    const indexPosition = localToRoot(
        fixture.root,
        rig.right.fingers.index[0].node.getWorldPosition(new THREE.Vector3())
    );
    const beforeAngle = thumbDistalSignedAngle(fixture, rig, 'right');

    applyFingerCurlState(rig.right, states.right, 'thumb', 1);

    const after = tipPosition(fixture, 'right', 'thumb');
    const afterAngle = thumbDistalSignedAngle(fixture, rig, 'right');
    t.diagnostic(`VRM 0.x maximum distal: ${formatThumbMeasurement(
        thumbDistalMeasurement(fixture, rig, 'right')
    )}`);
    assert.ok(afterAngle > beforeAngle + THREE.MathUtils.degToRad(40));
    assert.ok(afterAngle > THREE.MathUtils.degToRad(45));
    assert.ok(after.distanceTo(center) < before.distanceTo(center));
    assert.ok(after.distanceTo(indexPosition) < before.distanceTo(indexPosition));
    assert.ok(rig.right.fingers.thumb.every(
        (joint) => joint.node.quaternion.toArray().every(Number.isFinite)
    ));
    assert.equal(states.right.fingerSpread, 0);
});

test('thumb opposition is mirrored between rotated left and right hands', () => {
    const fixture = makeHumanoid({
        ancestorRotation: new THREE.Euler(-0.22, 0.41, -0.16)
    });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    applyFingerCurlState(rig.left, states.left, 'thumb', 1);
    applyFingerCurlState(rig.right, states.right, 'thumb', 1);
    const left = tipPosition(fixture, 'left', 'thumb');
    const right = tipPosition(fixture, 'right', 'thumb');
    assertNear(left.x, -right.x, 1e-6, 'thumb longitudinal mirror');
    assertNear(left.y, right.y, 1e-6, 'thumb palm-depth mirror');
    assertNear(left.z, right.z, 1e-6, 'thumb opposition mirror');
    assertNear(
        thumbDistalSignedAngle(fixture, rig, 'left'),
        thumbDistalSignedAngle(fixture, rig, 'right'),
        1e-6,
        'thumb distal flexion mirror'
    );
});

test('incomplete thumb rig remains finite and does not affect other fingers', () => {
    const missing = new Set(['leftThumbProximal']);
    const fixture = makeHumanoid({ sides: ['left'], missing });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const thumbNames = new Set(rig.left.fingers.thumb.map((joint) => joint.name));
    const untouched = quaternionSnapshot(rig, thumbNames);
    assert.doesNotThrow(() => {
        applyFingerCurlState(rig.left, states.left, 'thumb', 1);
    });
    assert.ok(rig.left.fingers.thumb.every(
        (joint) => joint.node.quaternion.toArray().every(Number.isFinite)
    ));
    assertSnapshotUnchanged(untouched, fixture.bones);
    assert.equal(states.left.fingerSpread, 0);
});

test('finger spread fans four fingertips using proximal joints only', () => {
    const fixture = makeHumanoid({ sides: ['left'] });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const beforeDistance = tipPosition(fixture, 'left', 'index')
        .distanceTo(tipPosition(fixture, 'left', 'little'));
    const downstream = SPREAD_FINGER_NAMES.flatMap(
        (finger) => rig.left.fingers[finger].slice(1)
    );
    const downstreamBefore = downstream.map((joint) => joint.node.quaternion.clone());
    const thumbBefore = rig.left.fingers.thumb.map((joint) => joint.node.quaternion.clone());

    assert.equal(applyFingerSpreadState(rig.left, states.left, 1), true);
    const afterDistance = tipPosition(fixture, 'left', 'index')
        .distanceTo(tipPosition(fixture, 'left', 'little'));
    assert.ok(afterDistance > beforeDistance + 0.02, 'four fingertips did not fan outward');
    downstream.forEach((joint, index) => {
        assert.ok(joint.node.quaternion.angleTo(downstreamBefore[index]) < 1e-9);
    });
    rig.left.fingers.thumb.forEach((joint, index) => {
        assert.ok(joint.node.quaternion.angleTo(thumbBefore[index]) < 1e-9);
    });
    assertNear(estimateFingerSpread(rig.left), 1, 1e-6, 'spread estimate');
});

test('curl and finger spread remain independent in both edit orders', () => {
    for (const order of ['curl-first', 'spread-first']) {
        const fixture = makeHumanoid({ sides: ['left'] });
        const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
        const states = createFingerPoseStates(rig);
        const index = rig.left.fingers.index;
        if (order === 'curl-first') {
            applyFingerCurlState(rig.left, states.left, 'index', 0.7);
            applyFingerSpreadState(rig.left, states.left, 0.8);
        } else {
            applyFingerSpreadState(rig.left, states.left, 0.8);
            applyFingerCurlState(rig.left, states.left, 'index', 0.7);
        }
        assertNear(estimateFingerCurl(index), 0.7, 1e-6, `${order} curl`);
        assertNear(estimateFingerSpread(rig.left), 0.8, 1e-6, `${order} spread`);
        const state = handPoseState(rig, states).left;
        assertNear(state.fingers.index.value, 0.7, 1e-6, `${order} state curl`);
        assertNear(state.fingerSpread, 0.8, 1e-6, `${order} state spread`);
    }
});

test('changing one curl never changes finger spread or other finger values', () => {
    const fixture = makeHumanoid({ sides: ['left'] });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    applyFingerSpreadState(rig.left, states.left, 0.55);
    const before = handPoseState(rig, states).left;
    applyFingerCurlState(rig.left, states.left, 'index', 1);
    const after = handPoseState(rig, states).left;
    assertNear(after.fingerSpread, before.fingerSpread, 1e-6, 'spread changed with curl');
    for (const finger of FINGER_NAMES.filter((name) => name !== 'index')) {
        assertNear(
            after.fingers[finger].value,
            before.fingers[finger].value,
            1e-9,
            `${finger} changed with index curl`
        );
    }
});

test('finger spread is mirrored and ignores missing proximal bones', () => {
    const missing = new Set(['rightRingProximal']);
    const fixture = makeHumanoid({ missing });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const ringDownstream = rig.right.fingers.ring
        .filter((joint) => joint.index > 0)
        .map((joint) => joint.node.quaternion.clone());
    assert.doesNotThrow(() => applyFingerSpreadState(rig.right, states.right, 1));
    rig.right.fingers.ring
        .filter((joint) => joint.index > 0)
        .forEach((joint, index) => {
            assert.ok(joint.node.quaternion.angleTo(ringDownstream[index]) < 1e-9);
        });
    applyFingerSpreadState(rig.left, states.left, 1);
    const leftIndex = tipPosition(fixture, 'left', 'index');
    const rightIndex = tipPosition(fixture, 'right', 'index');
    assertNear(leftIndex.x, -rightIndex.x, 1e-6, 'spread longitudinal mirror');
    assertNear(leftIndex.z, rightIndex.z, 1e-6, 'spread lateral mirror');
});

test('manual rotation rebasing preserves arbitrary current rotations without drift', () => {
    const fixture = makeHumanoid({ sides: ['left'] });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const joints = rig.left.fingers.ring;
    joints[0].node.rotateY(0.17);
    const original = joints[0].node.quaternion.clone();
    rebaseFingerJoint(rig.left, states.left, joints[0].name);
    applyFingerCurlState(rig.left, states.left, 'ring', 0);
    assert.ok(joints[0].node.quaternion.angleTo(original) < 1e-7);

    applyFingerCurlState(rig.left, states.left, 'ring', 0.4);
    applyFingerSpreadState(rig.left, states.left, 0.4);
    const spreadOriginal = joints[0].node.quaternion.clone();
    applyFingerSpreadState(rig.left, states.left, 0.4);
    assert.ok(joints[0].node.quaternion.angleTo(spreadOriginal) < 1e-7);
});

test('non-finite curl and spread inputs are rejected without changing joints', () => {
    const fixture = makeHumanoid({ sides: ['left'] });
    const rig = buildFingerCurlRig(fixture.bones, fixture.neutral);
    const states = createFingerPoseStates(rig);
    const before = quaternionSnapshot(rig);
    assert.throws(
        () => applyFingerCurlState(rig.left, states.left, 'thumb', Number.NaN),
        /finite/
    );
    assert.throws(
        () => applyFingerSpreadState(
            rig.left,
            states.left,
            Number.POSITIVE_INFINITY
        ),
        /finite/
    );
    assertSnapshotUnchanged(before, fixture.bones);
});

test('world-axis fallback uses the inverse world rotation', () => {
    const root = new THREE.Object3D();
    root.quaternion.setFromEuler(new THREE.Euler(0.42, -0.37, 0.21));
    const joint = new THREE.Object3D();
    joint.quaternion.setFromEuler(new THREE.Euler(-0.19, 0.14, 0.33));
    root.add(joint);
    root.updateWorldMatrix(true, true);
    const fallbackWorld = new THREE.Vector3(0.3, -0.8, 0.45).normalize();
    const expected = fallbackWorld.clone().applyQuaternion(
        joint.getWorldQuaternion(new THREE.Quaternion()).invert()
    );
    const actual = localAxisFromWorld(
        joint,
        new THREE.Vector3(),
        fallbackWorld
    );
    assert.ok(actual.angleTo(expected) < 1e-10);
});
