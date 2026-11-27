import * as THREE from 'three';

const TRUNK_EPSILON = 1e-6;

function isFiniteVector(vector) {
    return vector instanceof THREE.Vector3 && vector.toArray().every(Number.isFinite);
}

function isFiniteQuaternion(quaternion) {
    return quaternion instanceof THREE.Quaternion &&
        quaternion.toArray().every(Number.isFinite);
}

/**
 * Finds a stable pelvis position near the requested position while keeping each
 * limb root inside its reachable annulus. Constraints are expressed as a root
 * offset from the pelvis and a fixed effector target.
 */
function constrainPelvisPosition(requestedPosition, constraints, iterations = 12) {
    if (!isFiniteVector(requestedPosition)) {
        throw new TypeError('The requested pelvis position must be finite.');
    }
    const validConstraints = constraints.filter((constraint) =>
        isFiniteVector(constraint?.rootOffset) &&
        isFiniteVector(constraint?.target) &&
        Number.isFinite(constraint?.maximumLength) &&
        constraint.maximumLength > TRUNK_EPSILON &&
        Number.isFinite(constraint?.minimumLength ?? 0) &&
        (constraint.minimumLength ?? 0) >= 0 &&
        (constraint.minimumLength ?? 0) <= constraint.maximumLength);
    const result = requestedPosition.clone();
    const correction = new THREE.Vector3();
    const root = new THREE.Vector3();
    const offset = new THREE.Vector3();

    for (let iteration = 0; iteration < Math.max(1, iterations); iteration += 1) {
        correction.set(0, 0, 0);
        let correctionCount = 0;
        for (const constraint of validConstraints) {
            root.copy(result).add(constraint.rootOffset);
            offset.copy(root).sub(constraint.target);
            const distance = offset.length();
            const minimumLength = constraint.minimumLength ?? 0;
            if (distance > constraint.maximumLength || distance < minimumLength) {
                if (distance <= TRUNK_EPSILON) {
                    if (isFiniteVector(constraint.minimumDirection) &&
                        constraint.minimumDirection.lengthSq() >
                            TRUNK_EPSILON * TRUNK_EPSILON) {
                        offset.copy(constraint.minimumDirection).normalize();
                    } else {
                        offset.set(0, 1, 0);
                    }
                } else {
                    offset.multiplyScalar(1 / distance);
                }
                const reachableDistance = THREE.MathUtils.clamp(
                    distance,
                    minimumLength,
                    constraint.maximumLength
                );
                const nearestRoot = constraint.target.clone()
                    .addScaledVector(offset, reachableDistance);
                correction.add(nearestRoot.sub(root));
                correctionCount += 1;
            }
        }
        if (correctionCount === 0) {
            break;
        }
        result.addScaledVector(correction, 1 / correctionCount);
        if (!isFiniteVector(result)) {
            throw new Error('Pelvis constraint solving produced a non-finite position.');
        }
    }
    return result;
}

function applyWorldRotation(bone, worldDelta) {
    const parentWorld = bone.parent?.getWorldQuaternion(new THREE.Quaternion()) ??
        new THREE.Quaternion();
    const localDelta = parentWorld.clone().invert()
        .multiply(worldDelta)
        .multiply(parentWorld);
    bone.quaternion.premultiply(localDelta).normalize();
    bone.updateWorldMatrix(false, true);
    return isFiniteQuaternion(bone.quaternion);
}

/**
 * CCD solver for a variable-length normalized humanoid spine. The final entry
 * is the neck effector; preceding entries distribute the rotation.
 */
function solveSpineCcd(chain, target, options = {}) {
    if (!Array.isArray(chain) || chain.length < 2 || !chain.every(Boolean)) {
        return { reached: false, iterations: 0, distance: Number.POSITIVE_INFINITY };
    }
    if (!isFiniteVector(target)) {
        throw new TypeError('The spine target must be a finite Vector3.');
    }

    const maximumIterations = THREE.MathUtils.clamp(
        Math.trunc(options.maximumIterations ?? 12),
        1,
        32
    );
    const tolerance = Math.max(Number(options.tolerance) || 0.001, TRUNK_EPSILON);
    const maximumStep = THREE.MathUtils.degToRad(
        THREE.MathUtils.clamp(Number(options.maximumStepDegrees) || 12, 1, 35)
    );
    const effector = chain.at(-1);
    let finalDistance = Number.POSITIVE_INFINITY;
    let completedIterations = 0;

    for (let iteration = 0; iteration < maximumIterations; iteration += 1) {
        completedIterations = iteration + 1;
        for (let index = chain.length - 2; index >= 0; index -= 1) {
            const joint = chain[index];
            joint.updateWorldMatrix(true, true);
            const jointPosition = joint.getWorldPosition(new THREE.Vector3());
            const effectorPosition = effector.getWorldPosition(new THREE.Vector3());
            const toEffector = effectorPosition.sub(jointPosition);
            const toTarget = target.clone().sub(jointPosition);
            if (toEffector.lengthSq() <= TRUNK_EPSILON * TRUNK_EPSILON ||
                toTarget.lengthSq() <= TRUNK_EPSILON * TRUNK_EPSILON) {
                continue;
            }

            toEffector.normalize();
            toTarget.normalize();
            const angle = Math.acos(THREE.MathUtils.clamp(toEffector.dot(toTarget), -1, 1));
            if (angle <= TRUNK_EPSILON) {
                continue;
            }
            const weight = 0.55 + 0.35 * (index + 1) / Math.max(chain.length - 1, 1);
            const appliedAngle = Math.min(angle * weight, maximumStep);
            const axis = new THREE.Vector3().crossVectors(toEffector, toTarget);
            if (axis.lengthSq() <= TRUNK_EPSILON * TRUNK_EPSILON) {
                axis.set(0, 1, 0).cross(toEffector);
                if (axis.lengthSq() <= TRUNK_EPSILON * TRUNK_EPSILON) {
                    axis.set(1, 0, 0).cross(toEffector);
                }
            }
            axis.normalize();
            if (!applyWorldRotation(
                joint,
                new THREE.Quaternion().setFromAxisAngle(axis, appliedAngle))) {
                return { reached: false, iterations: completedIterations, distance: finalDistance };
            }
        }

        effector.updateWorldMatrix(true, false);
        finalDistance = effector.getWorldPosition(new THREE.Vector3()).distanceTo(target);
        if (!Number.isFinite(finalDistance)) {
            return { reached: false, iterations: completedIterations, distance: finalDistance };
        }
        if (finalDistance <= tolerance) {
            break;
        }
    }

    return {
        reached: finalDistance <= tolerance,
        iterations: completedIterations,
        distance: finalDistance
    };
}

export {
    constrainPelvisPosition,
    solveSpineCcd,
    TRUNK_EPSILON
};
