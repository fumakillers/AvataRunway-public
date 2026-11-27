import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';
import { applyTwoBoneIk, solveTwoBonePositions } from '../src/two-bone-ik.js';

const root = new THREE.Vector3(0, 0, 0);
const joint = new THREE.Vector3(1, 0, 0);
const end = new THREE.Vector3(2, 0, 0);

function assertFiniteSolution(solution) {
    assert.ok(solution.joint.toArray().every(Number.isFinite));
    assert.ok(solution.target.toArray().every(Number.isFinite));
    assert.ok(solution.bendDirection.toArray().every(Number.isFinite));
}

test('solves a reachable target while preserving both bone lengths', () => {
    const solution = solveTwoBonePositions(
        root,
        joint,
        end,
        new THREE.Vector3(1, 1, 0),
        new THREE.Vector3(0, 0, 1)
    );
    assert.ok(Math.abs(solution.joint.distanceTo(root) - 1) < 1e-9);
    assert.ok(Math.abs(solution.joint.distanceTo(solution.target) - 1) < 1e-9);
    assert.ok(solution.target.distanceTo(new THREE.Vector3(1, 1, 0)) < 1e-9);
    assertFiniteSolution(solution);
});

test('clamps a target beyond maximum reach without producing non-finite values', () => {
    const solution = solveTwoBonePositions(root, joint, end, new THREE.Vector3(100, 0, 0));
    assert.ok(solution.target.length() < 2);
    assert.ok(solution.target.length() > 1.999);
    assertFiniteSolution(solution);
});

test('handles a target extremely close to the root', () => {
    const solution = solveTwoBonePositions(
        root,
        joint,
        end,
        new THREE.Vector3(1e-15, 0, 0),
        new THREE.Vector3(0, 0, 1)
    );
    assert.ok(solution.target.length() > 0);
    assertFiniteSolution(solution);
});

test('uses the preferred bend direction for a nearly straight chain', () => {
    const forward = solveTwoBonePositions(
        root,
        joint,
        end,
        new THREE.Vector3(1.8, 0, 0),
        new THREE.Vector3(0, 0, 1)
    );
    const backward = solveTwoBonePositions(
        root,
        joint,
        end,
        new THREE.Vector3(1.8, 0, 0),
        new THREE.Vector3(0, 0, -1)
    );
    assert.ok(forward.joint.z > 0);
    assert.ok(backward.joint.z < 0);
    assertFiniteSolution(forward);
    assertFiniteSolution(backward);
});

test('applies the analytical result to an Object3D bone chain', () => {
    const rootBone = new THREE.Object3D();
    const jointBone = new THREE.Object3D();
    const endBone = new THREE.Object3D();
    jointBone.position.set(1, 0, 0);
    endBone.position.set(1, 0, 0);
    rootBone.add(jointBone);
    jointBone.add(endBone);
    const target = new THREE.Vector3(1, 1, 0);

    const solution = applyTwoBoneIk(
        rootBone,
        jointBone,
        endBone,
        target,
        new THREE.Vector3(0, 0, 1)
    );
    rootBone.updateWorldMatrix(true, true);
    const actualEnd = endBone.getWorldPosition(new THREE.Vector3());

    assert.ok(solution);
    assert.ok(actualEnd.distanceTo(target) < 1e-9);
    assert.ok(rootBone.quaternion.toArray().every(Number.isFinite));
    assert.ok(jointBone.quaternion.toArray().every(Number.isFinite));
});

for (const [name, rootPosition, jointPosition, endPosition, bend] of [
    ['left arm', new THREE.Vector3(0, 1, 0), new THREE.Vector3(-1, 1, 0), new THREE.Vector3(-2, 1, 0), new THREE.Vector3(0, 0, -1)],
    ['right arm', new THREE.Vector3(0, 1, 0), new THREE.Vector3(1, 1, 0), new THREE.Vector3(2, 1, 0), new THREE.Vector3(0, 0, -1)],
    ['left leg', new THREE.Vector3(-0.2, 1, 0), new THREE.Vector3(-0.2, 0.5, 0), new THREE.Vector3(-0.2, 0, 0), new THREE.Vector3(0, 0, 1)],
    ['right leg', new THREE.Vector3(0.2, 1, 0), new THREE.Vector3(0.2, 0.5, 0), new THREE.Vector3(0.2, 0, 0), new THREE.Vector3(0, 0, 1)]
]) {
    test(`${name} keeps its configured bend side`, () => {
        const target = endPosition.clone().add(new THREE.Vector3(0.05, 0.05, 0));
        const solution = solveTwoBonePositions(
            rootPosition,
            jointPosition,
            endPosition,
            target,
            bend
        );
        assert.ok(solution.bendDirection.dot(bend) > 0.99);
        assertFiniteSolution(solution);
    });
}
