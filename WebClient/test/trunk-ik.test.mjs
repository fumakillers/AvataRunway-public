import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';
import { constrainPelvisPosition, solveSpineCcd } from '../src/trunk-ik.js';

function createSpine(segmentCount = 4) {
    const root = new THREE.Object3D();
    const chain = [];
    let parent = root;
    for (let index = 0; index < segmentCount; index += 1) {
        const bone = new THREE.Object3D();
        bone.position.set(0, index === 0 ? 0 : 1, 0);
        parent.add(bone);
        chain.push(bone);
        parent = bone;
    }
    root.updateWorldMatrix(true, true);
    return { root, chain };
}

test('pelvis constraint keeps both foot roots inside their reachable spheres', () => {
    const constraints = [
        {
            rootOffset: new THREE.Vector3(-0.3, 0, 0),
            target: new THREE.Vector3(-0.4, -2, 0),
            maximumLength: 2.1
        },
        {
            rootOffset: new THREE.Vector3(0.3, 0, 0),
            target: new THREE.Vector3(0.4, -2, 0),
            maximumLength: 2.1
        }
    ];
    const result = constrainPelvisPosition(new THREE.Vector3(1.5, 1.5, 0), constraints);

    assert.ok(result.toArray().every(Number.isFinite));
    for (const constraint of constraints) {
        const distance = result.clone().add(constraint.rootOffset).distanceTo(constraint.target);
        assert.ok(distance <= constraint.maximumLength + 1e-4);
    }
});

test('pelvis constraint keeps a leg root outside its minimum reach', () => {
    const constraint = {
        rootOffset: new THREE.Vector3(),
        target: new THREE.Vector3(),
        minimumLength: 0.75,
        maximumLength: 2,
        minimumDirection: new THREE.Vector3(0, 1, 0)
    };
    const near = constrainPelvisPosition(new THREE.Vector3(0, 0.1, 0), [constraint]);
    const coincident = constrainPelvisPosition(new THREE.Vector3(), [constraint]);

    assert.ok(Math.abs(near.distanceTo(constraint.target) - 0.75) < 1e-10);
    assert.ok(Math.abs(coincident.distanceTo(constraint.target) - 0.75) < 1e-10);
    assert.ok(coincident.y > 0);
});

test('conflicting pelvis constraints return a finite stable nearest position', () => {
    const constraints = [
        {
            rootOffset: new THREE.Vector3(-0.2, 0, 0),
            target: new THREE.Vector3(-4, 0, 0),
            maximumLength: 1
        },
        {
            rootOffset: new THREE.Vector3(0.2, 0, 0),
            target: new THREE.Vector3(4, 0, 0),
            maximumLength: 1
        }
    ];
    const first = constrainPelvisPosition(new THREE.Vector3(0, 0, 0), constraints);
    const second = constrainPelvisPosition(new THREE.Vector3(0, 0, 0), constraints);
    assert.ok(first.toArray().every(Number.isFinite));
    assert.ok(first.distanceTo(second) < 1e-12);
});

test('spine CCD moves the neck toward the target and distributes rotation', () => {
    const { chain } = createSpine(4);
    const target = new THREE.Vector3(1.2, 2.4, 0);
    const initialDistance = chain.at(-1).getWorldPosition(new THREE.Vector3()).distanceTo(target);

    const result = solveSpineCcd(chain, target);
    const finalDistance = chain.at(-1).getWorldPosition(new THREE.Vector3()).distanceTo(target);
    const rotatedJoints = chain.slice(0, -1).filter((bone) =>
        1 - Math.abs(bone.quaternion.w) > 1e-5);

    assert.ok(finalDistance < initialDistance);
    assert.ok(rotatedJoints.length >= 2);
    assert.ok(result.iterations <= 12);
    assert.ok(chain.every((bone) => bone.quaternion.toArray().every(Number.isFinite)));
});

test('spine CCD handles backward and unreachable targets without flipping or non-finite values', () => {
    for (const target of [
        new THREE.Vector3(-1, 2.2, 0),
        new THREE.Vector3(100, -50, 20),
        new THREE.Vector3(0, 0.000001, 0)
    ]) {
        const { chain } = createSpine(4);
        const result = solveSpineCcd(chain, target, { maximumIterations: 10 });
        assert.ok(result.iterations <= 10);
        assert.ok(Number.isFinite(result.distance));
        assert.ok(chain.every((bone) => bone.quaternion.toArray().every(Number.isFinite)));
    }
});

test('spine CCD supports a missing upper-chest chain', () => {
    const { chain } = createSpine(3);
    const result = solveSpineCcd(chain, new THREE.Vector3(0.8, 1.6, 0));
    assert.ok(result.iterations > 0);
    assert.ok(Number.isFinite(result.distance));
});
