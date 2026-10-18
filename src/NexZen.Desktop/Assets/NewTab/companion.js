import * as THREE from 'three';
import { GLTFLoader } from './vendor/GLTFLoader.js';
import { RoomEnvironment } from './vendor/RoomEnvironment.js';

console.log('[NewTab] companion module start');

const params = new URLSearchParams(location.search);
const embed = params.has('embed');
const studio = params.has('studio');
const ai = params.has('ai');
const widget = embed || ai;
const lockCenter = ai;
const docked = studio || widget;
if (docked) document.documentElement.classList.add(studio ? 'studio' : 'embed');
const STORE_KEY = studio
  ? 'nexzen.companion.placement.studio'
  : (ai ? 'nexzen.companion.placement.ai'
    : (embed ? 'nexzen.companion.placement.widget' : 'nexzen.companion.placement'));
const STORE_URL = studio
  ? './placement-studio.json'
  : (ai ? './placement-ai.json'
    : (embed ? './placement-widget.json' : './placement.json'));
const MIN_ZOOM = 0.25;
const MAX_ZOOM = 4;
const LIGHT_STORE_KEY = 'nexzen.companion.lighting';
const LIGHT_STORE_URL = './lighting.json';
const LIGHT_DEFAULTS = {
  exposure: 1.2,
  highlights: 0,
  even: 0,
  env: 1.15,
  hemi: 1.15,
  key: 2.4,
  fill: 1.25,
  rim: 1.1,
  saturation: 1,
  vibrance: 0,
  panelOpen: true
};
const LIGHT_KEYS = ['exposure', 'highlights', 'even', 'env', 'hemi', 'key', 'fill', 'rim', 'saturation', 'vibrance'];
const lighting = { ...LIGHT_DEFAULTS };
const BG_HEX = docked ? 0xf3f3f3 : 0xffffff;

const boot = document.getElementById('boot');
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(32, 1, 0.05, 40);
scene.add(camera);

const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
renderer.setClearColor(BG_HEX, 1);
if (docked) {
  document.documentElement.style.background = '#f3f3f3';
  document.body.style.background = '#f3f3f3';
}
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.NoToneMapping;
renderer.toneMappingExposure = 1;
renderer.domElement.style.cursor = 'default';
document.body.appendChild(renderer.domElement);

{
  const pmrem = new THREE.PMREMGenerator(renderer);
  const room = new RoomEnvironment(renderer);
  scene.environment = pmrem.fromScene(room, 0.04).texture;
  room.dispose();
  pmrem.dispose();
}

const ambient = new THREE.AmbientLight(0xffffff, 0);
scene.add(ambient);
const wrapLights = [
  [1, 0.2, 0.35],
  [-1, 0.15, 0.4],
  [0.2, 1, 0.15],
  [0.15, -0.85, 0.35],
  [0.1, 0.25, 1],
  [0.25, 0.4, -1]
].map(([x, y, z]) => {
  const light = new THREE.DirectionalLight(0xffffff, 0);
  light.position.set(x, y, z);
  camera.add(light);
  return light;
});
const form = new THREE.DirectionalLight(0xfff6ec, 0);
form.position.set(-0.28, 0.72, 1.15);
camera.add(form);
const hemi = new THREE.HemisphereLight(0xffffff, 0xd4cfc8, 1.15);
camera.add(hemi);
const key = new THREE.DirectionalLight(0xfff6ec, 2.4);
key.position.set(-0.35, 0.95, 1.35);
camera.add(key);
const fill = new THREE.DirectionalLight(0xe8f0ff, 1.25);
fill.position.set(0.85, 0.25, 0.65);
camera.add(fill);
const rim = new THREE.DirectionalLight(0xfff3ff, 1.1);
rim.position.set(0.45, 0.55, -0.85);
camera.add(rim);

const colorTarget = new THREE.WebGLRenderTarget(1, 1, {
  format: THREE.RGBAFormat,
  type: THREE.HalfFloatType,
  minFilter: THREE.LinearFilter,
  magFilter: THREE.LinearFilter,
  samples: 4
});
const colorMaterial = new THREE.ShaderMaterial({
  uniforms: {
    tDiffuse: { value: null },
    exposure: { value: 1 },
    highlights: { value: 0 },
    saturation: { value: 1 },
    vibrance: { value: 0 }
  },
  transparent: true,
  toneMapped: false,
  vertexShader: `
    varying vec2 vUv;
    void main() {
      vUv = uv;
      gl_Position = vec4(position.xy, 0.0, 1.0);
    }
  `,
  fragmentShader: `
    uniform sampler2D tDiffuse;
    uniform float exposure;
    uniform float highlights;
    uniform float saturation;
    uniform float vibrance;
    varying vec2 vUv;

    vec3 agxContrast(vec3 x) {
      vec3 x2 = x * x;
      vec3 x4 = x2 * x2;
      return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4
        - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
    }

    vec3 agxMap(vec3 color) {
      const mat3 toRec2020 = mat3(
        vec3(0.6274, 0.0691, 0.0164),
        vec3(0.3293, 0.9195, 0.0880),
        vec3(0.0433, 0.0113, 0.8956)
      );
      const mat3 toSrgb = mat3(
        vec3(1.6605, -0.1246, -0.0182),
        vec3(-0.5876, 1.1329, -0.1006),
        vec3(-0.0728, -0.0083, 1.1187)
      );
      const mat3 inset = mat3(
        vec3(0.856627153315983, 0.137318972929847, 0.11189821299995),
        vec3(0.0951212405381588, 0.761241990602591, 0.0767994186031903),
        vec3(0.0482516061458583, 0.101439036467562, 0.811302368396859)
      );
      const mat3 outset = mat3(
        vec3(1.1271005818144368, -0.1413297634984383, -0.14132976349843826),
        vec3(-0.11060664309660323, 1.157823702216272, -0.11060664309660294),
        vec3(-0.016493938717834573, -0.016493938717834257, 1.2519364065950405)
      );
      const float minEv = -12.47393;
      const float maxEv = 4.026069;
      color = toRec2020 * color;
      color = inset * color;
      color = max(color, 1e-10);
      color = log2(color);
      color = clamp((color - minEv) / (maxEv - minEv), 0.0, 1.0);
      color = agxContrast(color);
      color = outset * color;
      color = pow(max(color, 0.0), vec3(2.2));
      return toSrgb * color;
    }

    void main() {
      vec4 texel = texture2D(tDiffuse, vUv);
      float mask = smoothstep(0.02, 0.18, texel.a);
      if (mask < 0.001) {
        gl_FragColor = vec4(0.0);
        return;
      }
      vec3 src = texel.rgb / max(texel.a, 1e-5);
      vec3 mapped = agxMap(src * max(exposure, 0.0));
      float luma = dot(mapped, vec3(0.2126, 0.7152, 0.0722));
      float hiMask = smoothstep(0.40, 0.92, luma);
      float pull = clamp(highlights, -1.0, 1.0);
      mapped *= 1.0 - hiMask * max(-pull, 0.0) * 0.78;
      mapped += mapped * hiMask * max(pull, 0.0) * 0.40;
      float gLuma = dot(mapped, vec3(0.2126, 0.7152, 0.0722));
      vec3 satColor = mix(vec3(gLuma), mapped, saturation);
      float chroma = max(satColor.r, max(satColor.g, satColor.b)) - min(satColor.r, min(satColor.g, satColor.b));
      float vibe = 1.0 + vibrance * (1.0 - chroma);
      vec3 graded = clamp(mix(vec3(gLuma), satColor, vibe), 0.0, 1.0);
      gl_FragColor = vec4(graded, mask);
    }
  `,
  depthTest: false,
  depthWrite: false
});
const colorScene = new THREE.Scene();
const colorCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
colorScene.add(new THREE.Mesh(new THREE.PlaneGeometry(2, 2), colorMaterial));

const holder = new THREE.Group();
camera.add(holder);

const raycaster = new THREE.Raycaster();
const pointerNdc = new THREE.Vector2();
const pointer = { x: 0, y: 0 };
const look = { x: 0, y: 0 };
let blink = 1;
let nextBlink = 1.6 + Math.random() * 2.4;
let elapsed = 0;
let eyeMeshes = [];
let blinkMorphs = [];
let companion = null;
let animReady = false;
let dragging = false;
let dragOffset = { x: 0, y: 0 };
let hoverModel = false;
let saveTimer = 0;
let lightSaveTimer = 0;
let lightPanel = null;
let lightToggle = null;
let lightStatus = null;

const placement = {
  x: null,
  y: null,
  zoom: 1
};

function setBoot(text) {
  if (boot) boot.textContent = text || '';
}

function clamp(v, a, b) {
  return Math.min(b, Math.max(a, v));
}

function viewSize(dist) {
  const vFov = THREE.MathUtils.degToRad(camera.fov);
  const height = 2 * Math.tan(vFov / 2) * dist;
  return { width: height * camera.aspect, height };
}

function screenToPlane(clientX, clientY) {
  const dist = Math.abs(holder.position.z) || 1.85;
  const { width, height } = viewSize(dist);
  const ndcX = (clientX / window.innerWidth) * 2 - 1;
  const ndcY = -((clientY / window.innerHeight) * 2 - 1);
  return { x: ndcX * width / 2, y: ndcY * height / 2 };
}

function syncPlacementFromHolder() {
  const dist = Math.abs(holder.position.z) || 1.85;
  const { width, height } = viewSize(dist);
  placement.x = clamp((holder.position.x / (width / 2) + 1) / 2, 0.04, 0.96);
  placement.y = clamp((1 - holder.position.y / (height / 2)) / 2, 0.04, 0.96);
}

function applyPlacement() {
  const dist = 1.85;
  const { width, height } = viewSize(dist);
  const defaultSize = Math.min(width, height) * (docked ? 0.72 : 0.36);
  if (lockCenter) {
    placement.x = 0.50;
    placement.y = 0.50;
  }
  if (placement.x == null || placement.y == null) {
    if (docked) {
      placement.x = studio ? 0.64 : 0.50;
      placement.y = studio ? 0.58 : (lockCenter ? 0.50 : 0.66);
      if (placement.zoom === 1) placement.zoom = studio ? 1.15 : 1.2;
      holder.position.set(
        (placement.x * 2 - 1) * width / 2,
        (1 - placement.y * 2) * height / 2,
        -dist
      );
    } else {
      const size = defaultSize * placement.zoom;
      holder.position.set(
        width * 0.5 - size * 0.55 - width * 0.035,
        -height * 0.5 + size * 0.5 + height * 0.045,
        -dist
      );
      syncPlacementFromHolder();
    }
  } else {
    holder.position.set(
      (placement.x * 2 - 1) * width / 2,
      (1 - placement.y * 2) * height / 2,
      -dist
    );
  }
  if (docked && lighting.panelOpen && !lockCenter)
    holder.position.x += width * (studio ? 0.12 : 0.16);
  const size = defaultSize * placement.zoom;
  holder.scale.setScalar(size / 0.42);
  holder.userData.baseY = holder.position.y;
}

function scheduleSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(savePlacement, 180);
}

function savePlacement() {
  syncPlacementFromHolder();
  const data = { x: placement.x, y: placement.y, zoom: placement.zoom };
  try { localStorage.setItem(STORE_KEY, JSON.stringify(data)); } catch {}
  fetch(STORE_URL, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data)
  }).catch(() => {});
}

function readStored(raw) {
  if (!raw) return;
  const data = typeof raw === 'string' ? JSON.parse(raw) : raw;
  if (!lockCenter && Number.isFinite(data.x) && Number.isFinite(data.y)) {
    placement.x = clamp(data.x, 0.04, 0.96);
    placement.y = clamp(data.y, 0.04, 0.96);
  }
  if (Number.isFinite(data.zoom))
    placement.zoom = clamp(data.zoom, MIN_ZOOM, MAX_ZOOM);
}

async function loadPlacement() {
  try {
    const res = await fetch(STORE_URL, { cache: 'no-store' });
    if (res.ok) readStored(await res.json());
  } catch {}
  if (placement.x == null) {
    try { readStored(localStorage.getItem(STORE_KEY)); } catch {}
  }
}

function fitInHolder(root) {
  const box = new THREE.Box3().setFromObject(root);
  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const longest = Math.max(size.x, size.y, size.z, 0.0001);
  const wrap = new THREE.Group();
  wrap.add(root);
  root.position.sub(center);
  wrap.scale.setScalar(0.42 / longest);
  root.traverse((node) => {
    if (!node.isMesh) return;
    const mats = Array.isArray(node.material) ? node.material : [node.material];
    for (const mat of mats) {
      if (!mat) continue;
      if ('envMapIntensity' in mat) mat.envMapIntensity = lighting.env;
      if ('specularIntensity' in mat && mat.specularIntensity > 0.6)
        mat.specularIntensity = 0.6;
    }
  });
  return wrap;
}

function collectEyes(root) {
  eyeMeshes = [];
  blinkMorphs = [];
  root.traverse((node) => {
    if (!node.isMesh) return;
    const name = (node.name || '').toLowerCase();
    if (/(^|[_-\s])eye/.test(name) || name.includes('pupil') || name.includes('iris'))
      eyeMeshes.push(node);
    const dict = node.morphTargetDictionary;
    const infl = node.morphTargetInfluences;
    if (!dict || !infl) return;
    for (const key of Object.keys(dict)) {
      if (/blink|eyeclose|eyesclosed|wink/i.test(key))
        blinkMorphs.push({ infl, index: dict[key] });
    }
  });
}

function makeFallback() {
  const group = new THREE.Group();
  const body = new THREE.Mesh(
    new THREE.SphereGeometry(0.5, 48, 32),
    new THREE.MeshStandardMaterial({ color: 0x2a2d36, roughness: 0.35, metalness: 0.08 })
  );
  group.add(body);
  const eyeWhite = new THREE.MeshStandardMaterial({ color: 0xf4f6ff, roughness: 0.25, metalness: 0 });
  const pupilMat = new THREE.MeshStandardMaterial({ color: 0x111111, roughness: 0.4, metalness: 0 });
  const shineMat = new THREE.MeshBasicMaterial({ color: 0xffffff });

  function makeEye(x) {
    const eye = new THREE.Group();
    eye.position.set(x, 0.12, 0.38);
    const white = new THREE.Mesh(new THREE.SphereGeometry(0.11, 24, 16), eyeWhite);
    white.scale.set(1, 1.08, 0.72);
    const pupil = new THREE.Mesh(new THREE.SphereGeometry(0.045, 16, 12), pupilMat);
    pupil.position.z = 0.07;
    const shine = new THREE.Mesh(new THREE.SphereGeometry(0.016, 10, 8), shineMat);
    shine.position.set(0.018, 0.02, 0.1);
    eye.add(white, pupil, shine);
    eye.userData.pupil = pupil;
    return eye;
  }

  const left = makeEye(-0.13);
  const right = makeEye(0.13);
  group.add(left, right);
  group.userData.eyes = [left, right];
  return group;
}

function applyLookAndBlink(dt) {
  if (!dragging) {
    look.x += (pointer.x - look.x) * Math.min(1, dt * 6);
    look.y += (pointer.y - look.y) * Math.min(1, dt * 6);
  }
  holder.rotation.y = look.x * 0.35;
  holder.rotation.x = -look.y * 0.22;

  nextBlink -= dt;
  if (nextBlink <= 0) {
    blink = 0.08;
    nextBlink = 1.8 + Math.random() * 3.2;
  }
  blink += (1 - blink) * Math.min(1, dt * 14);

  if (!dragging) {
    const baseY = holder.userData.baseY ?? holder.position.y;
    holder.position.y = baseY + Math.sin(elapsed * 1.15) * 0.012;
  }

  for (const morph of blinkMorphs)
    morph.infl[morph.index] = 1 - blink;

  if (companion?.userData.eyes) {
    for (const eye of companion.userData.eyes) {
      eye.scale.y = blink;
      const pupil = eye.userData.pupil;
      if (pupil) {
        pupil.position.x = look.x * 0.035;
        pupil.position.y = look.y * 0.03;
      }
    }
  } else {
    for (const mesh of eyeMeshes)
      mesh.scale.y = THREE.MathUtils.lerp(mesh.scale.y, blink, 0.35);
  }
}

function setPointerFromEvent(e) {
  pointer.x = (e.clientX / window.innerWidth) * 2 - 1;
  pointer.y = -((e.clientY / window.innerHeight) * 2 - 1);
  pointerNdc.set(pointer.x, pointer.y);
}

function hitsModel() {
  if (!companion) return false;
  camera.updateMatrixWorld();
  holder.updateMatrixWorld();
  raycaster.setFromCamera(pointerNdc, camera);
  return raycaster.intersectObject(companion, true).length > 0;
}

function updateCursor() {
  if (lockCenter) {
    renderer.domElement.style.cursor = 'default';
    return;
  }
  renderer.domElement.style.cursor = dragging ? 'grabbing' : (hoverModel ? 'grab' : 'default');
}

async function loadGlb() {
  const loader = new GLTFLoader();
  const gltf = await loader.loadAsync('./companion.glb');
  const fitted = fitInHolder(gltf.scene);
  collectEyes(fitted);
  if (gltf.animations?.length) {
    const mixer = new THREE.AnimationMixer(gltf.scene);
    mixer.clipAction(gltf.animations[0]).play();
    fitted.userData.mixer = mixer;
  }
  console.log('[NewTab] glb loaded');
  return fitted;
}

function applyLighting() {
  const even = Math.max(0, lighting.even);
  const flatten = Math.min(1, even / 2);
  const dirKeep = 1 - flatten * 0.4;
  ambient.intensity = even * 0.22;
  form.intensity = even * 0.55;
  const wrapEach = even * 0.32;
  for (const light of wrapLights) light.intensity = wrapEach;
  hemi.intensity = lighting.hemi * dirKeep;
  key.intensity = lighting.key * dirKeep;
  fill.intensity = lighting.fill * dirKeep;
  rim.intensity = lighting.rim * dirKeep;
  colorMaterial.uniforms.exposure.value = lighting.exposure;
  colorMaterial.uniforms.highlights.value = lighting.highlights;
  colorMaterial.uniforms.saturation.value = lighting.saturation;
  colorMaterial.uniforms.vibrance.value = lighting.vibrance;
  if (animReady) markInteractive();
  if (!companion) return;
  companion.traverse((node) => {
    if (!node.isMesh) return;
    const mats = Array.isArray(node.material) ? node.material : [node.material];
    for (const mat of mats) {
      if (mat && 'envMapIntensity' in mat) mat.envMapIntensity = lighting.env;
    }
  });
}

function lightingPayload() {
  return {
    exposure: lighting.exposure,
    highlights: lighting.highlights,
    even: lighting.even,
    env: lighting.env,
    hemi: lighting.hemi,
    key: lighting.key,
    fill: lighting.fill,
    rim: lighting.rim,
    saturation: lighting.saturation,
    vibrance: lighting.vibrance,
    panelOpen: lighting.panelOpen
  };
}

async function saveLighting() {
  const data = lightingPayload();
  const body = JSON.stringify(data);
  try { localStorage.setItem(LIGHT_STORE_KEY, body); } catch {}
  try {
    const res = await fetch(LIGHT_STORE_URL, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      cache: 'no-store',
      body
    });
    console.log('[NewTab] lighting saved', res.status, body);
  } catch (err) {
    console.error('[NewTab] lighting save failed', err);
  }
  if (lightStatus) {
    lightStatus.textContent = 'Saved';
    clearTimeout(lightStatus._t);
    lightStatus._t = setTimeout(() => { if (lightStatus) lightStatus.textContent = ''; }, 900);
  }
}

function scheduleLightSave() {
  clearTimeout(lightSaveTimer);
  lightSaveTimer = setTimeout(saveLighting, 180);
}

function readLighting(raw) {
  if (!raw) return;
  const data = typeof raw === 'string' ? JSON.parse(raw) : raw;
  for (const key of LIGHT_KEYS) {
    if (Number.isFinite(data[key])) lighting[key] = data[key];
  }
  if (typeof data.panelOpen === 'boolean') lighting.panelOpen = data.panelOpen;
}

async function loadLighting() {
  let loaded = false;
  try {
    const res = await fetch(LIGHT_STORE_URL + '?t=' + Date.now(), { cache: 'no-store' });
    if (res.ok) {
      const data = await res.json();
      console.log('[NewTab] lighting loaded', data);
      if (data && typeof data === 'object' && Object.keys(data).length) {
        readLighting(data);
        loaded = true;
      }
    }
  } catch (err) {
    console.error('[NewTab] lighting load failed', err);
  }
  if (!loaded) {
    try { readLighting(localStorage.getItem(LIGHT_STORE_KEY)); } catch {}
  }
}

function setLightPanelOpen(open) {
  lighting.panelOpen = open;
  if (lightPanel) lightPanel.classList.toggle('hidden', !open);
  if (lightToggle) lightToggle.classList.toggle('hidden', open);
  if (docked) resize();
}

function bindSlider(id, key, min, max, step) {
  const input = lightPanel.querySelector('#' + id);
  const value = lightPanel.querySelector('#' + id + '-val');
  input.min = String(min);
  input.max = String(max);
  input.step = String(step);
  input.value = String(lighting[key]);
  const paint = () => { value.textContent = Number(lighting[key]).toFixed(2); };
  paint();
  input.addEventListener('input', () => {
    lighting[key] = Number(input.value);
    paint();
    applyLighting();
    scheduleLightSave();
  });
}

function createLightPanel() {
  lightToggle = document.createElement('button');
  lightToggle.id = 'light-toggle';
  lightToggle.type = 'button';
  lightToggle.title = 'Lighting (L)';
  lightToggle.textContent = '☀';
  document.body.appendChild(lightToggle);

  lightPanel = document.createElement('div');
  lightPanel.id = 'light-panel';
  lightPanel.innerHTML = `
    <div class="head">
      <strong>Lighting</strong>
      <button type="button" id="light-close" title="Hide">✕</button>
    </div>
    <label>Exposure <input id="ls-exposure" type="range"> <span id="ls-exposure-val"></span></label>
    <label>Highlights <input id="ls-highlights" type="range"> <span id="ls-highlights-val"></span></label>
    <label>Even <input id="ls-even" type="range"> <span id="ls-even-val"></span></label>
    <label>Environment <input id="ls-env" type="range"> <span id="ls-env-val"></span></label>
    <label>Hemisphere <input id="ls-hemi" type="range"> <span id="ls-hemi-val"></span></label>
    <label>Key <input id="ls-key" type="range"> <span id="ls-key-val"></span></label>
    <label>Fill <input id="ls-fill" type="range"> <span id="ls-fill-val"></span></label>
    <label>Rim <input id="ls-rim" type="range"> <span id="ls-rim-val"></span></label>
    <label>Saturation <input id="ls-saturation" type="range"> <span id="ls-saturation-val"></span></label>
    <label>Vibrance <input id="ls-vibrance" type="range"> <span id="ls-vibrance-val"></span></label>
    <div class="foot">
      <button type="button" id="light-reset">Reset</button>
      <span class="status" id="light-status"></span>
      <button type="button" id="light-save">Save</button>
    </div>`;
  document.body.appendChild(lightPanel);
  lightStatus = lightPanel.querySelector('#light-status');

  bindSlider('ls-exposure', 'exposure', 0.2, 3, 0.05);
  bindSlider('ls-highlights', 'highlights', -1, 1, 0.05);
  bindSlider('ls-even', 'even', 0, 2, 0.05);
  bindSlider('ls-env', 'env', 0, 4, 0.05);
  bindSlider('ls-hemi', 'hemi', 0, 4, 0.05);
  bindSlider('ls-key', 'key', 0, 8, 0.05);
  bindSlider('ls-fill', 'fill', 0, 6, 0.05);
  bindSlider('ls-rim', 'rim', 0, 6, 0.05);
  bindSlider('ls-saturation', 'saturation', 0, 2, 0.05);
  bindSlider('ls-vibrance', 'vibrance', -1, 1, 0.05);

  const block = (e) => e.stopPropagation();
  lightPanel.addEventListener('pointerdown', block);
  lightPanel.addEventListener('pointermove', block);
  lightPanel.addEventListener('wheel', block, { passive: true });
  lightToggle.addEventListener('pointerdown', block);

  lightToggle.addEventListener('click', () => {
    setLightPanelOpen(true);
    scheduleLightSave();
  });
  lightPanel.querySelector('#light-close').addEventListener('click', () => {
    setLightPanelOpen(false);
    saveLighting();
  });
  lightPanel.querySelector('#light-save').addEventListener('click', async () => {
    await saveLighting();
    if (studio) console.log('[Studio] close');
    else setLightPanelOpen(false);
  });
  lightPanel.querySelector('#light-reset').addEventListener('click', () => {
    const open = lighting.panelOpen;
    Object.assign(lighting, LIGHT_DEFAULTS);
    lighting.panelOpen = open;
    for (const key of LIGHT_KEYS) {
      const input = lightPanel.querySelector('#ls-' + key);
      const el = lightPanel.querySelector('#ls-' + key + '-val');
      if (input) input.value = lighting[key];
      if (el) el.textContent = Number(lighting[key]).toFixed(2);
    }
    applyLighting();
    saveLighting();
  });

  window.addEventListener('keydown', (e) => {
    if (e.key !== 'l' && e.key !== 'L') return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    setLightPanelOpen(!lighting.panelOpen);
    scheduleLightSave();
  });

  setLightPanelOpen(lighting.panelOpen);
}

window.reloadCompanionLighting = async () => {
  await loadLighting();
  if (widget) lighting.panelOpen = false;
  applyLighting();
  if (lockCenter) applyPlacement();
  console.log('[NewTab] lighting applied', lightingPayload());
};

document.addEventListener('visibilitychange', () => {
  if (document.hidden) stopAnim();
  else markInteractive();
});

await Promise.all([loadPlacement(), loadLighting()]);
if (widget) lighting.panelOpen = false;
if (studio) lighting.panelOpen = true;
applyLighting();
if (!widget) createLightPanel();
companion = makeFallback();
holder.add(companion);
applyLighting();
setBoot('');

function resize() {
  const w = Math.max(1, window.innerWidth);
  const h = Math.max(1, window.innerHeight);
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.setSize(w, h, false);
  colorTarget.setSize(renderer.domElement.width, renderer.domElement.height);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  applyPlacement();
}
window.addEventListener('resize', () => {
  resize();
  if (typeof markInteractive === 'function')
    markInteractive();
});
resize();

renderer.domElement.addEventListener('pointerdown', (e) => {
  if (e.button !== 0) return;
  setPointerFromEvent(e);
  markInteractive();
  if (lockCenter) return;
  if (!hitsModel()) return;
  const plane = screenToPlane(e.clientX, e.clientY);
  dragOffset.x = holder.position.x - plane.x;
  dragOffset.y = holder.position.y - plane.y;
  dragging = true;
  renderer.domElement.setPointerCapture(e.pointerId);
  updateCursor();
  e.preventDefault();
});

renderer.domElement.addEventListener('pointermove', (e) => {
  setPointerFromEvent(e);
  markInteractive();
  if (dragging) {
    const plane = screenToPlane(e.clientX, e.clientY);
    holder.position.x = plane.x + dragOffset.x;
    holder.position.y = plane.y + dragOffset.y;
    holder.userData.baseY = holder.position.y;
    syncPlacementFromHolder();
    applyPlacement();
    updateCursor();
    return;
  }
  hoverModel = hitsModel();
  updateCursor();
});

function endDrag(e) {
  if (!dragging) return;
  dragging = false;
  try { renderer.domElement.releasePointerCapture(e.pointerId); } catch {}
  syncPlacementFromHolder();
  applyPlacement();
  savePlacement();
  hoverModel = hitsModel();
  updateCursor();
}

renderer.domElement.addEventListener('pointerup', endDrag);
renderer.domElement.addEventListener('pointercancel', endDrag);

renderer.domElement.addEventListener('wheel', (e) => {
  e.preventDefault();
  markInteractive();
  const factor = e.deltaY > 0 ? 0.92 : 1.08;
  placement.zoom = clamp(placement.zoom * factor, MIN_ZOOM, MAX_ZOOM);
  applyPlacement();
  scheduleSave();
}, { passive: false });

const clock = new THREE.Clock();
const IDLE_FREEZE_MS = widget ? 1800 : 0;
let animating = false;
let rafId = 0;
let idleAt = performance.now();
animReady = true;

function markInteractive() {
  if (!animReady)
    return;
  idleAt = performance.now();
  startAnim();
}

function stopAnim() {
  animating = false;
  if (rafId) {
    cancelAnimationFrame(rafId);
    rafId = 0;
  }
}

function startAnim() {
  if (!animReady || document.hidden || animating)
    return;
  animating = true;
  clock.getDelta();
  rafId = requestAnimationFrame(frame);
}

function renderOnce() {
  const dt = Math.min(0.05, clock.getDelta());
  elapsed += dt;
  companion?.userData.mixer?.update(dt);
  applyLookAndBlink(dt);
  renderer.setClearColor(0x000000, 0);
  renderer.setRenderTarget(colorTarget);
  renderer.render(scene, camera);
  renderer.setRenderTarget(null);
  renderer.setClearColor(BG_HEX, 1);
  colorMaterial.uniforms.tDiffuse.value = colorTarget.texture;
  renderer.render(colorScene, colorCamera);
}

function frame() {
  rafId = 0;
  if (document.hidden) {
    animating = false;
    return;
  }
  if (IDLE_FREEZE_MS && !dragging && performance.now() - idleAt > IDLE_FREEZE_MS) {
    renderOnce();
    animating = false;
    return;
  }
  renderOnce();
  rafId = requestAnimationFrame(frame);
}

markInteractive();

loadGlb().then((model) => {
  holder.remove(companion);
  companion = model;
  holder.add(companion);
  applyPlacement();
  applyLighting();
  markInteractive();
}).catch((err) => {
  console.error('[NewTab] glb failed', err);
});
