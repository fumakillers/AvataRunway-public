import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from '@pixiv/three-vrm';
import {
    calculateVrmBodyTarget,
    isCameraResetShortcut,
    VrmCameraController
} from './camera-controller.js';
import { buildVersionedMetadata } from './metadata-builder.js';
import {
    buildGridBackgroundImage,
    resolveColorScheme
} from './color-schemes.js';
import { PoseController } from './pose-controller.js';

const viewport = document.getElementById('viewport');
const statusElement = document.getElementById('status');
const pendingFiles = new Map();

let currentVrm = null;
let isLoading = false;
let viewerInputEnabled = true;
let dragDepth = 0;
let statusTimer = 0;
let scene = null;
let camera = null;
let perspectiveCamera = null;
let orthographicCamera = null;
let cameraController = null;
let renderer = null;
let clock = null;
let viewerInitializationError = null;
let pointerInteraction = null;
let poseController = null;
const observedDragStages = new Set();

const ROTATION_RADIANS_PER_PIXEL = 0.005;
const FIFTEEN_DEGREES = THREE.MathUtils.degToRad(15);
const KEYBOARD_ZOOM_DELTA = 100;

function postMessage(message) {
    if (window.chrome?.webview) {
        window.chrome.webview.postMessage(message);
    }
}

function postDiagnostic(stage, detail = '') {
    postMessage({ type: 'diagnostic', stage, detail });
}

function postDragDiagnosticOnce(stage) {
    if (observedDragStages.has(stage)) {
        return;
    }

    observedDragStages.add(stage);
    postDiagnostic(stage);
}

postDiagnostic('viewer-bundle-loaded');
console.info('[AvataRunway] viewer-bundle.js loaded');

function showStatus(message, kind = 'info', autoHide = false) {
    window.clearTimeout(statusTimer);
    statusElement.textContent = message;
    statusElement.className = kind;
    statusElement.hidden = false;

    if (autoHide) {
        statusTimer = window.setTimeout(() => {
            statusElement.hidden = true;
        }, 2200);
    }
}

function reportError(fileName, methodName, userMessage, error, token = '') {
    const detail = error instanceof Error
        ? `${error.message}${error.stack ? ` | ${error.stack}` : ''}`
        : String(error ?? userMessage);

    showStatus(
        currentVrm ? `${userMessage}\n現在のVRMは維持されています。` : userMessage,
        'error'
    );
    postMessage({
        type: 'load-error',
        token,
        fileName,
        className: 'VrmViewer',
        methodName,
        userMessage,
        detail
    });
}

function readFileAsArrayBuffer(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onprogress = (event) => {
            if (event.lengthComputable) {
                const percent = Math.round((event.loaded / event.total) * 100);
                showStatus(`VRMを読み込み中… ${percent}%`);
            } else {
                showStatus('VRMを読み込み中…');
            }
        };
        reader.onerror = () => reject(reader.error ?? new Error('ファイルの読み込みに失敗しました。'));
        reader.onabort = () => reject(new Error('ファイルの読み込みが中断されました。'));
        reader.onload = () => resolve(reader.result);
        reader.readAsArrayBuffer(file);
    });
}

function parseVrm(arrayBuffer) {
    const loader = new GLTFLoader();
    loader.register((parser) => new VRMLoaderPlugin(parser));
    return loader.parseAsync(arrayBuffer, '');
}

function detectVrmVersion(gltf) {
    const json = gltf.parser?.json;
    const extensions = json?.extensions;
    const vrm1SpecVersion = extensions?.VRMC_vrm?.specVersion;
    if (vrm1SpecVersion === '1.0') {
        return 'VRM 1.0';
    }

    const vrm0SpecVersion = extensions?.VRM?.specVersion;
    if (typeof vrm0SpecVersion === 'string' && /^0\./.test(vrm0SpecVersion)) {
        return 'VRM 0.x';
    }

    throw new Error('VRM 0.x／VRM 1.0の内部拡張情報を確認できませんでした。');
}

function buildMetadata(fileName, versionLabel, vrm) {
    const meta = vrm.meta;
    if (meta?.metaVersion === '0') {
        return buildVersionedMetadata(fileName, versionLabel, meta);
    }

    if (meta?.metaVersion === '1') {
        return buildVersionedMetadata(fileName, versionLabel, meta);
    }

    throw new Error('three-vrmがVRMメタ情報のバージョンを判定できませんでした。');
}

function buildBoneTree(vrm) {
    const humanBones = vrm.humanoid?.rawHumanBones ?? {};
    const entries = Object.entries(humanBones)
        .filter(([, bone]) => bone?.node)
        .map(([name, bone]) => ({ name, node: bone.node }));
    const nameByNode = new Map(entries.map((entry) => [entry.node, entry.name]));
    const treeByName = new Map(entries.map((entry) => [entry.name, { name: entry.name, children: [] }]));
    const roots = [];

    for (const entry of entries) {
        let parent = entry.node.parent;
        while (parent && !nameByNode.has(parent)) {
            parent = parent.parent;
        }

        const node = treeByName.get(entry.name);
        const parentName = parent ? nameByNode.get(parent) : null;
        if (parentName && treeByName.has(parentName)) {
            treeByName.get(parentName).children.push(node);
        } else {
            roots.push(node);
        }
    }

    const sortNodes = (nodes) => {
        nodes.sort((a, b) => a.name.localeCompare(b.name));
        nodes.forEach((node) => sortNodes(node.children));
    };
    sortNodes(roots);
    return roots;
}

function buildExpressions(vrm) {
    const expressionManager = vrm.expressionManager;
    if (!expressionManager) {
        return [];
    }

    return expressionManager.expressions
        .map((expression) => expression?.expressionName)
        .filter((name) => typeof name === 'string' && name.length > 0)
        .map((name) => ({ name, value: expressionManager.getValue(name) ?? 0 }));
}

function fitCameraToModel(vrm) {
    const model = vrm.scene;
    model.updateWorldMatrix(true, true);
    const box = new THREE.Box3().setFromObject(model);
    if (box.isEmpty()) {
        throw new Error('モデルのBounding Boxを計算できませんでした。');
    }

    const baseTarget = calculateVrmBodyTarget(vrm, box);
    cameraController.fitToBox(box, baseTarget);
    camera = cameraController.camera;
}

function disposeVrm(vrm) {
    if (!vrm) {
        return;
    }

    scene?.remove(vrm.scene);
    VRMUtils.deepDispose(vrm.scene);
}

async function loadPendingFile(token) {
    const file = pendingFiles.get(token);
    pendingFiles.delete(token);
    if (!file) {
        reportError('', 'loadPendingFile', '読み込み対象のファイルを取得できませんでした。', new Error('The pending file was not found.'), token);
        return;
    }

    if (isLoading) {
        reportError(file.name, 'loadPendingFile', '別のVRMを読み込み中です。完了後にもう一度お試しください。', new Error('Another VRM load is already in progress.'), token);
        return;
    }

    if (viewerInitializationError || !scene || !camera || !renderer) {
        reportError(
            file.name,
            'loadPendingFile',
            'ビューアを初期化できないため、VRMを読み込めませんでした。',
            viewerInitializationError ?? new Error('The Three.js viewer is not initialized.'),
            token
        );
        return;
    }

    isLoading = true;
    poseController?.setModelLoading(true);
    postMessage({ type: 'model-loading', isLoading: true });
    let candidateGltf = null;
    let candidateVrm = null;
    try {
        showStatus('VRMを読み込み中… 0%');
        const arrayBuffer = await readFileAsArrayBuffer(file);
        showStatus('VRMを解析中…');
        candidateGltf = await parseVrm(arrayBuffer);
        const versionLabel = detectVrmVersion(candidateGltf);
        candidateVrm = candidateGltf.userData.vrm;
        if (!candidateVrm) {
            throw new Error('three-vrmがVRMデータを生成できませんでした。');
        }

        if (candidateVrm.meta?.metaVersion === '0') {
            VRMUtils.rotateVRM0(candidateVrm);
        }

        const metadata = buildMetadata(file.name, versionLabel, candidateVrm);
        const bones = buildBoneTree(candidateVrm);
        scene.add(candidateVrm.scene);
        fitCameraToModel(candidateVrm);
        candidateVrm.expressionManager?.resetValues();
        candidateVrm.expressionManager?.update();
        const expressions = buildExpressions(candidateVrm);

        const previousVrm = currentVrm;
        currentVrm = candidateVrm;
        candidateVrm = null;
        poseController?.setVrm(currentVrm);
        disposeVrm(previousVrm);

        showStatus(`${file.name} を読み込みました。`, 'success', true);
        postMessage({ type: 'load-success', token, metadata, bones, expressions });
    } catch (error) {
        if (candidateVrm) {
            disposeVrm(candidateVrm);
        } else if (candidateGltf?.scene) {
            VRMUtils.deepDispose(candidateGltf.scene);
        }
        reportError(file.name, 'loadPendingFile', 'VRMの読み込みに失敗しました。', error, token);
    } finally {
        isLoading = false;
        poseController?.setModelLoading(false);
        postMessage({ type: 'model-loading', isLoading: false });
    }
}

async function loadExternalVrm(url, fileName, token, failureMessage = '前回のVRMを自動読み込みできませんでした。ファイルを選び直してください。') {
    try {
        showStatus('前回のVRMを読み込み中…');
        const response = await fetch(url, { cache: 'no-store' });
        postDiagnostic(
            'FetchResponse',
            `Token=${token} Status=${response.status} StatusText=${response.statusText} Ok=${response.ok}`
        );
        if (!response.ok) {
            throw new Error(`VRM file request failed (${response.status} ${response.statusText}).`);
        }

        const arrayBuffer = await response.arrayBuffer();
        pendingFiles.set(token, new File([arrayBuffer], fileName, { type: 'model/gltf-binary' }));
        await loadPendingFile(token);
    } catch (error) {
        reportError(
            fileName,
            'loadExternalVrm',
            failureMessage,
            error,
            token
        );
    }
}

function showAutoLoadError(message) {
    showStatus(message, 'error');
}

function cancelPendingFile(token) {
    pendingFiles.delete(token);
    if (currentVrm) {
        showStatus('新しいVRMの読み込みをキャンセルしました。', 'info', true);
    } else {
        showStatus('VRMファイルをここへドラッグ＆ドロップしてください。');
    }
}

function rejectPendingFile(token, message) {
    pendingFiles.delete(token);
    showStatus(
        currentVrm ? `${message}\n現在のVRMは維持されています。` : message,
        'error'
    );
}

function handleDrop(event) {
    event.preventDefault();
    if (!viewerInputEnabled) {
        return;
    }

    postDragDiagnosticOnce('viewer-drop-observed');
    dragDepth = 0;
    document.body.classList.remove('drag-active');

    const file = event.dataTransfer?.files?.[0];
    if (!file) {
        reportError('', 'handleDrop', 'ドロップされたファイルを取得できませんでした。');
        return;
    }

    const token = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
    pendingFiles.set(token, file);
    postMessage({ type: 'drop-request', token, fileName: file.name, isLoading });
}

document.addEventListener('dragenter', (event) => {
    event.preventDefault();
    if (!viewerInputEnabled) {
        return;
    }

    postDragDiagnosticOnce('viewer-dragenter-observed');
    dragDepth += 1;
    document.body.classList.add('drag-active');
});

document.addEventListener('dragover', (event) => {
    event.preventDefault();
    if (!viewerInputEnabled) {
        if (event.dataTransfer) {
            event.dataTransfer.dropEffect = 'none';
        }
        return;
    }

    postDragDiagnosticOnce('viewer-dragover-observed');
    if (event.dataTransfer) {
        event.dataTransfer.dropEffect = 'copy';
    }
});

document.addEventListener('dragleave', (event) => {
    event.preventDefault();
    dragDepth = Math.max(0, dragDepth - 1);
    if (dragDepth === 0) {
        document.body.classList.remove('drag-active');
    }
});

document.addEventListener('drop', handleDrop);
postDiagnostic(
    'drag-handlers-registered',
    'document dragenter, dragover, dragleave, and drop handlers registered'
);
console.info('[AvataRunway] drag handlers registered');

function isEditableTarget(target) {
    return target instanceof Element && Boolean(
        target.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"]')
    );
}

function isPreviewKeyboardActive() {
    const activeElement = document.activeElement;
    return activeElement === viewport || viewport.contains(activeElement);
}

function endPointerInteraction(event = null) {
    if (!pointerInteraction || (event && event.pointerId !== pointerInteraction.pointerId)) {
        return;
    }

    const pointerId = pointerInteraction.pointerId;
    if (pointerInteraction.mode === 'ik') {
        poseController?.endHandleDrag(pointerId);
    }
    pointerInteraction = null;
    if (viewport.hasPointerCapture?.(pointerId)) {
        viewport.releasePointerCapture(pointerId);
    }
}

function handlePointerDown(event) {
    if (!viewerInputEnabled) {
        return;
    }

    let mode = null;
    if (poseController?.beginHandleDrag(event, cameraController?.camera, renderer?.domElement)) {
        mode = 'ik';
    } else if (event.button === 2) {
        mode = 'rotate';
    } else if (event.button === 1 || (event.button === 0 && event.shiftKey)) {
        mode = 'pan';
    }

    if (!mode || !cameraController) {
        return;
    }

    event.preventDefault();
    viewport.focus({ preventScroll: true });
    pointerInteraction = {
        pointerId: event.pointerId,
        button: event.button,
        mode,
        x: event.clientX,
        y: event.clientY
    };
    viewport.setPointerCapture?.(event.pointerId);
}

function handlePointerMove(event) {
    if (!viewerInputEnabled) {
        return;
    }

    if (!pointerInteraction || event.pointerId !== pointerInteraction.pointerId) {
        poseController?.updateHover(event, cameraController?.camera, renderer?.domElement);
        return;
    }

    const expectedButtonMask = pointerInteraction.button === 0
        ? 1
        : pointerInteraction.button === 1 ? 4 : 2;
    if ((event.buttons & expectedButtonMask) === 0) {
        endPointerInteraction(event);
        return;
    }

    event.preventDefault();
    const deltaX = event.clientX - pointerInteraction.x;
    const deltaY = event.clientY - pointerInteraction.y;
    pointerInteraction.x = event.clientX;
    pointerInteraction.y = event.clientY;

    if (pointerInteraction.mode === 'ik') {
        poseController?.moveHandleDrag(event, cameraController.camera, renderer.domElement);
    } else if (pointerInteraction.mode === 'rotate') {
        cameraController.orbit(
            -deltaX * ROTATION_RADIANS_PER_PIXEL,
            deltaY * ROTATION_RADIANS_PER_PIXEL
        );
    } else {
        cameraController.panPixels(deltaX, deltaY, viewport.clientHeight);
    }
}

function handlePointerEnd(event) {
    if (pointerInteraction?.pointerId === event.pointerId) {
        event.preventDefault();
    }
    endPointerInteraction(event);
}

function handlePointerLeave() {
    if (!pointerInteraction) {
        poseController?.clearHover();
    }
}

function handleWheel(event) {
    if (!viewerInputEnabled) {
        return;
    }

    if (!cameraController || event.deltaY === 0) {
        return;
    }

    event.preventDefault();
    viewport.focus({ preventScroll: true });
    cameraController.zoom(event.deltaY);
}

function handleCameraKeyDown(event) {
    if (!viewerInputEnabled || !cameraController || !isPreviewKeyboardActive() || isEditableTarget(event.target)) {
        return;
    }

    if (event.code === 'KeyR' && !event.ctrlKey && !event.altKey && !event.metaKey) {
        if (isCameraResetShortcut(event) && cameraController.resetToInitialView()) {
            event.preventDefault();
            camera = cameraController.camera;
        }
        return;
    }

    if (event.ctrlKey && !event.altKey && !event.metaKey) {
        if (event.code === 'KeyZ') {
            const changed = event.shiftKey
                ? poseController?.redo()
                : poseController?.undo();
            if (changed) {
                event.preventDefault();
            }
        } else if (event.code === 'KeyY') {
            if (poseController?.redo()) {
                event.preventDefault();
            }
        } else if (event.code === 'Semicolon' || event.code === 'NumpadAdd') {
            event.preventDefault();
            cameraController.zoom(-KEYBOARD_ZOOM_DELTA);
        } else if (event.code === 'Minus' || event.code === 'NumpadSubtract') {
            event.preventDefault();
            cameraController.zoom(KEYBOARD_ZOOM_DELTA);
        }
        return;
    }

    if (event.altKey || event.metaKey || event.shiftKey) {
        return;
    }

    const match = /^(?:Digit|Numpad)([1-9])$/.exec(event.code);
    if (!match) {
        return;
    }

    event.preventDefault();
    switch (match[1]) {
        case '1':
            cameraController.viewFront();
            break;
        case '2':
            cameraController.orbit(0, -FIFTEEN_DEGREES);
            break;
        case '3':
            cameraController.viewRightFront();
            break;
        case '4':
            cameraController.orbit(-FIFTEEN_DEGREES, 0);
            break;
        case '5':
            cameraController.toggleProjection();
            camera = cameraController.camera;
            break;
        case '6':
            cameraController.orbit(FIFTEEN_DEGREES, 0);
            break;
        case '7':
            cameraController.viewTop();
            break;
        case '8':
            cameraController.orbit(0, FIFTEEN_DEGREES);
            break;
        case '9':
            cameraController.flip();
            break;
    }
}

function registerCameraInputHandlers() {
    viewport.tabIndex = 0;
    viewport.addEventListener('pointerdown', handlePointerDown);
    viewport.addEventListener('pointermove', handlePointerMove);
    viewport.addEventListener('pointerleave', handlePointerLeave);
    viewport.addEventListener('pointerup', handlePointerEnd);
    viewport.addEventListener('pointercancel', handlePointerEnd);
    viewport.addEventListener('lostpointercapture', handlePointerEnd);
    viewport.addEventListener('wheel', handleWheel, { passive: false });
    viewport.addEventListener('contextmenu', (event) => event.preventDefault());
    document.addEventListener('keydown', handleCameraKeyDown);
    window.addEventListener('blur', () => endPointerInteraction());
}

function setInputEnabled(value) {
    viewerInputEnabled = value === 'true';
    if (!viewerInputEnabled) {
        dragDepth = 0;
        document.body.classList.remove('drag-active');
        endPointerInteraction();
        viewport.blur();
    }
}

function setPoseMode(value) {
    poseController?.setPoseMode(value === true || value === 'true');
}

function selectPoseBone(name) {
    return poseController?.setSelectedBone(name) ?? false;
}

function setPoseBoneEuler(name, x, y, z) {
    return poseController?.setBoneEuler(name, x, y, z) ?? false;
}

function resetPoseBone(name) {
    return poseController?.resetBone(name) ?? false;
}

function resetPoseAll() {
    poseController?.resetAll();
}

function undoPose() {
    return poseController?.undo() ?? false;
}

function redoPose() {
    return poseController?.redo() ?? false;
}

function selectPoseHand(side) {
    return poseController?.selectPoseHand(side) ?? false;
}

function selectPoseFinger(side, finger) {
    return poseController?.selectPoseFinger(side, finger) ?? false;
}

function beginFingerCurl(side, finger) {
    return poseController?.beginFingerCurl(side, finger) ?? false;
}

function setFingerCurl(side, finger, amount) {
    return poseController?.setFingerCurl(side, finger, amount) ?? false;
}

function endFingerCurl(side, finger) {
    return poseController?.endFingerCurl(side, finger) ?? false;
}

function beginPoseFingerSpread(side) {
    return poseController?.beginFingerSpread(side) ?? false;
}

function setPoseFingerSpread(side, amount) {
    return poseController?.setFingerSpread(side, amount) ?? false;
}

function endPoseFingerSpread(side) {
    return poseController?.endFingerSpread(side) ?? false;
}

function copyPoseFingers(side) {
    return poseController?.copyFingersToOtherHand(side) ?? false;
}

function resetPoseHandFingers(side) {
    return poseController?.resetHandFingers(side) ?? false;
}

function getCurrentPose() {
    return poseController?.getCurrentPose() ?? null;
}

function applyPose(poseJson) {
    return poseController?.applyPose(poseJson) ?? [];
}

function setColorScheme(name) {
    const scheme = resolveColorScheme(name);
    document.body.style.backgroundColor = scheme.backgroundColor;
    document.body.style.backgroundImage = buildGridBackgroundImage(scheme);
}

function setExpressionValue(expressionName, value) {
    const expressionManager = currentVrm?.expressionManager;
    const numericValue = Number(value);
    if (!expressionManager || !Number.isFinite(numericValue)) {
        return;
    }

    expressionManager.setValue(expressionName, Math.min(Math.max(numericValue, 0.0), 1.0));
    expressionManager.update();
}

function resetExpressionValues() {
    const expressionManager = currentVrm?.expressionManager;
    if (!expressionManager) {
        return;
    }

    expressionManager.resetValues();
    expressionManager.update();
}

function resizeViewer() {
    if (!cameraController || !renderer) {
        return;
    }

    const width = Math.max(viewport.clientWidth, 1);
    const height = Math.max(viewport.clientHeight, 1);
    cameraController.resize(width / height);
    camera = cameraController.camera;
    renderer.setSize(width, height, false);
}

window.addEventListener('resize', resizeViewer);

window.avatarRunway = {
    startPendingLoad: loadPendingFile,
    cancelPendingLoad: cancelPendingFile,
    rejectPendingLoad: rejectPendingFile,
    loadExternalVrm,
    showAutoLoadError,
    setInputEnabled,
    setColorScheme,
    setExpressionValue,
    resetExpressionValues,
    setPoseMode,
    selectPoseBone,
    setPoseBoneEuler,
    resetPoseBone,
    resetPoseAll,
    undoPose,
    redoPose,
    selectPoseHand,
    selectPoseFinger,
    beginFingerCurl,
    setFingerCurl,
    endFingerCurl,
    beginPoseFingerSpread,
    setPoseFingerSpread,
    endPoseFingerSpread,
    copyPoseFingers,
    resetPoseHandFingers,
    getCurrentPose,
    applyPose,
    getCameraState: () => cameraController?.snapshot() ?? null
};

function animate() {
    requestAnimationFrame(animate);
    clock.update();
    const delta = Math.min(clock.getDelta(), 0.1);
    if (currentVrm) {
        currentVrm.update(delta);
    }
    cameraController.apply();
    poseController?.updateHandleScale(cameraController.camera, viewport.clientHeight);
    renderer.render(scene, cameraController.camera);
}

function initializeViewer() {
    scene = new THREE.Scene();
    perspectiveCamera = new THREE.PerspectiveCamera(35, 1, 0.01, 1000);
    orthographicCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.01, 1000);
    cameraController = new VrmCameraController(perspectiveCamera, orthographicCamera);
    camera = cameraController.camera;
    poseController = new PoseController(scene, postMessage);
    renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    viewport.appendChild(renderer.domElement);

    const light = new THREE.DirectionalLight(0xffffff, Math.PI);
    light.position.set(1.0, 1.0, 1.0).normalize();
    scene.add(light);

    clock = new THREE.Timer();
    registerCameraInputHandlers();
    resizeViewer();
    animate();
    postMessage({ type: 'viewer-ready' });
}

try {
    initializeViewer();
} catch (error) {
    viewerInitializationError = error;
    reportError('', 'initializeViewer', 'ビューアの初期化に失敗しました。', error);
}
