import * as THREE from 'three';

const IK_EPSILON = 1e-6;

function finiteVector(vector) {
    return vector instanceof THREE.Vector3 && vector.toArray().every(Number.isFinite);
}

function stablePerpendicular(direction, preferredDirection = null) {
    const perpendicular = preferredDirection?.clone() ?? new THREE.Vector3();
    perpendicular.addScaledVector(direction, -perpendicular.dot(direction));
    if (perpendicular.lengthSq() > IK_EPSILON * IK_EPSILON) {
        return perpendicular.normalize();
    }

    const axis = Math.abs(direction.y) < 0.9
        ? new THREE.Vector3(0, 1, 0)
        : new THREE.Vector3(1, 0, 0);
    perpendicular.copy(axis).addScaledVector(direction, -axis.dot(direction));
    if (perpendicular.lengthSq() <= IK_EPSILON * IK_EPSILON) {
        perpendicular.set(0, 0, 1);
    }
    return perpendicular.normalize();
}

/**
 * Solves the triangle for a two-bone chain without mutating the supplied vectors.
 * The returned target is clamped to the reachable annulus and the preferred bend
 * direction determines which of the two valid joint positions is chosen.
 */
function solveTwoBonePositions(root, joint, end, target, preferredBendDirection = null) {
    if (![root, joint, end, target].every(finiteVector)) {
        throw new TypeError('Two-bone IK inputs must be finite Vector3 values.');
    }

    const upperLength = root.distanceTo(joint);
    const lowerLength = joint.distanceTo(end);
    if (upperLength <= IK_EPSILON || lowerLength <= IK_EPSILON) {
        throw new RangeError('Two-bone IK requires non-zero bone lengths.');
    }

    const targetOffset = target.clone().sub(root);
    let requestedDistance = targetOffset.length();
    const direction = requestedDistance > IK_EPSILON
        ? targetOffset.multiplyScalar(1 / requestedDistance)
        : end.clone().sub(root).normalize();
    if (direction.lengthSq() <= IK_EPSILON * IK_EPSILON) {
        direction.set(0, 1, 0);
    }

    const minimumDistance = Math.abs(upperLength - lowerLength) + IK_EPSILON;
    const maximumDistance = Math.max(upperLength + lowerLength - IK_EPSILON, minimumDistance);
    requestedDistance = THREE.MathUtils.clamp(requestedDistance, minimumDistance, maximumDistance);

    const clampedTarget = root.clone().addScaledVector(direction, requestedDistance);
    const currentJointOffset = joint.clone().sub(root);
    const bendHint = preferredBendDirection?.clone() ?? currentJointOffset;
    const bendDirection = stablePerpendicular(direction, bendHint);

    const along = THREE.MathUtils.clamp(
        (upperLength * upperLength + requestedDistance * requestedDistance - lowerLength * lowerLength) /
            (2 * requestedDistance),
        -upperLength,
        upperLength
    );
    const heightSquared = Math.max(upperLength * upperLength - along * along, 0);
    const height = Math.sqrt(heightSquared);
    const solvedJoint = root.clone()
        .addScaledVector(direction, along)
        .addScaledVector(bendDirection, height);

    if (![solvedJoint, clampedTarget, bendDirection].every(finiteVector)) {
        throw new Error('Two-bone IK produced a non-finite result.');
    }

    return {
        joint: solvedJoint,
        target: clampedTarget,
        bendDirection,
        upperLength,
        lowerLength
    };
}

function applyWorldDirectionRotation(bone, fromDirection, toDirection) {
    const from = fromDirection.clone().normalize();
    const to = toDirection.clone().normalize();
    if (from.lengthSq() <= IK_EPSILON * IK_EPSILON ||
        to.lengthSq() <= IK_EPSILON * IK_EPSILON) {
        return false;
    }

    const worldDelta = new THREE.Quaternion().setFromUnitVectors(from, to);
    const parentWorld = bone.parent?.getWorldQuaternion(new THREE.Quaternion()) ?? new THREE.Quaternion();
    const localDelta = parentWorld.clone().invert()
        .multiply(worldDelta)
        .multiply(parentWorld);
    bone.quaternion.premultiply(localDelta).normalize();
    bone.updateWorldMatrix(false, true);
    return bone.quaternion.toArray().every(Number.isFinite);
}

function applyTwoBoneIk(rootBone, jointBone, endBone, target, preferredBendDirection = null) {
    rootBone.updateWorldMatrix(true, true);
    const root = rootBone.getWorldPosition(new THREE.Vector3());
    const joint = jointBone.getWorldPosition(new THREE.Vector3());
    const end = endBone.getWorldPosition(new THREE.Vector3());
    const solution = solveTwoBonePositions(root, joint, end, target, preferredBendDirection);

    if (!applyWorldDirectionRotation(
        rootBone,
        joint.clone().sub(root),
        solution.joint.clone().sub(root))) {
        return null;
    }

    rootBone.updateWorldMatrix(true, true);
    const updatedJoint = jointBone.getWorldPosition(new THREE.Vector3());
    const updatedEnd = endBone.getWorldPosition(new THREE.Vector3());
    if (!applyWorldDirectionRotation(
        jointBone,
        updatedEnd.clone().sub(updatedJoint),
        solution.target.clone().sub(updatedJoint))) {
        return null;
    }

    rootBone.updateWorldMatrix(true, true);
    return solution;
}

export {
    applyTwoBoneIk,
    IK_EPSILON,
    solveTwoBonePositions,
    stablePerpendicular
};
