<template>
  <div ref="container" class="fixed inset-0 z-0 transition-opacity duration-1000 overflow-hidden pointer-events-none" :class="{ 'opacity-40': isReduced }">
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import * as THREE from 'three';

defineProps<{
  isReduced: boolean;
}>();

const container = ref<HTMLElement | null>(null);

let scene: THREE.Scene;
let camera: THREE.PerspectiveCamera | null = null;
let renderer: THREE.WebGLRenderer | null = null;
let knotMesh: THREE.Mesh | null = null;
let starfieldSmall: THREE.Points | null = null;
let starfieldLarge: THREE.Points | null = null;
let frameId: number;

let mouseX = 0, mouseY = 0;
const windowHalfX = window.innerWidth / 2;
const windowHalfY = window.innerHeight / 2;

const knotVertexShader = `
  varying vec3 vNormal;
  varying vec3 vViewPosition;
  varying vec2 vUv;
  void main() {
    vUv = uv;
    vNormal = normalize(normalMatrix * normal);
    vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
    vViewPosition = -mvPosition.xyz;
    gl_Position = projectionMatrix * mvPosition;
  }
`;

const knotFragmentShader = `
  uniform float time;
  uniform vec3 color;
  uniform vec3 glowColor;
  varying vec3 vNormal;
  varying vec3 vViewPosition;
  varying vec2 vUv;
  void main() {
    vec3 normal = normalize(vNormal);
    vec3 viewDir = normalize(vViewPosition);
    float fresnel = pow(1.0 - dot(viewDir, normal), 3.0);
    vec3 baseColor = color * 0.15;
    baseColor = mix(baseColor, glowColor * 0.5, fresnel);
    float rim = pow(1.0 - max(dot(viewDir, normal), 0.0), 8.0);
    baseColor += glowColor * rim * 0.8;
    float pulse = sin(time * 0.3) * 0.02 + 0.05;
    baseColor += glowColor * pulse;
    gl_FragColor = vec4(baseColor, 0.15 + fresnel * 0.4);
  }
`;

const starVertexShader = `
  attribute float size;
  attribute vec3 customColor;
  varying vec3 vColor;
  uniform float time;
  void main() {
    vColor = customColor;
    float twinkle = sin(time * 2.0 + position.x) * 0.5 + 0.5;
    vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
    gl_PointSize = size * (800.0 / -mvPosition.z) * (0.8 + twinkle * 0.4);
    gl_Position = projectionMatrix * mvPosition;
  }
`;

const starFragmentShader = `
  varying vec3 vColor;
  void main() {
    float r = distance(gl_PointCoord, vec2(0.5));
    if (r > 0.5) discard;
    float glow = exp(-r * 4.0);
    gl_FragColor = vec4(vColor * glow, glow);
  }
`;

const init = () => {
  if (!container.value) return;

  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x010103);
  scene.fog = new THREE.FogExp2(0x010103, 0.0006);

  camera = new THREE.PerspectiveCamera(60, window.innerWidth / window.innerHeight, 1, 5000);
  camera.position.z = 1200;

  renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setSize(window.innerWidth, window.innerHeight);
  container.value.appendChild(renderer.domElement);

  const knotGeo = new THREE.TorusKnotGeometry(260, 80, 250, 50, 2, 3);
  const knotMat = new THREE.ShaderMaterial({
    uniforms: {
      time: { value: 0 },
      color: { value: new THREE.Color(0x010306) },
      glowColor: { value: new THREE.Color(0x3b82f6) }
    },
    vertexShader: knotVertexShader,
    fragmentShader: knotFragmentShader,
    transparent: true,
    blending: THREE.AdditiveBlending,
    depthWrite: false
  });
  knotMesh = new THREE.Mesh(knotGeo, knotMat);
  knotMesh.position.x = 200; // Offset to the right
  scene.add(knotMesh);

  generateStarfield(3000, 4000, 1.5, 0.3, true);
  generateStarfield(800, 2000, 3.0, 0.8, false);

  window.addEventListener('resize', onWindowResize);
  document.addEventListener('mousemove', onMouseMove);
  animate();
};

const generateStarfield = (count: number, range: number, baseSize: number, minSize: number, isLarge: boolean) => {
  const geo = new THREE.BufferGeometry();
  const pos = new Float32Array(count * 3);
  const col = new Float32Array(count * 3);
  const siz = new Float32Array(count);

  for (let i = 0; i < count; i++) {
    const i3 = i * 3;
    pos[i3] = (Math.random() - 0.5) * range * 2;
    pos[i3+1] = (Math.random() - 0.5) * range * 2;
    pos[i3+2] = (Math.random() - 0.5) * range * 2;

    const color = new THREE.Color();
    color.setHSL(0.6 + Math.random() * 0.1, 0.7, 0.8);
    col[i3] = color.r; col[i3+1] = color.g; col[i3+2] = color.b;
    siz[i] = minSize + Math.random() * baseSize;
  }

  geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  geo.setAttribute('customColor', new THREE.BufferAttribute(col, 3));
  geo.setAttribute('size', new THREE.BufferAttribute(siz, 1));

  const mat = new THREE.ShaderMaterial({
    uniforms: { time: { value: 0 } },
    vertexShader: starVertexShader,
    fragmentShader: starFragmentShader,
    transparent: true,
    blending: THREE.AdditiveBlending,
    depthWrite: false
  });

  const points = new THREE.Points(geo, mat);
  scene.add(points);
  if (isLarge) starfieldLarge = points; else starfieldSmall = points;
};

const onMouseMove = (e: MouseEvent) => {
  mouseX = e.clientX - windowHalfX;
  mouseY = e.clientY - windowHalfY;
};

const onWindowResize = () => {
  if (!camera || !renderer) return;
  camera.aspect = window.innerWidth / window.innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);
};

const animate = () => {
  frameId = requestAnimationFrame(animate);
  if (!renderer || !scene || !camera) return;

  const time = performance.now() * 0.001;

  if (knotMesh) {
    knotMesh.rotation.y += 0.001;
    knotMesh.rotation.z += 0.0005;
    knotMesh.rotation.x += (mouseY * 0.0002 - knotMesh.rotation.x) * 0.05;
    knotMesh.rotation.y += (mouseX * 0.0002 - knotMesh.rotation.y) * 0.05;
    (knotMesh.material as any).uniforms.time.value = time;
  }

  if (starfieldSmall) {
    starfieldSmall.rotation.y += 0.0002;
    starfieldSmall.position.x += (mouseX * 0.02 - starfieldSmall.position.x) * 0.01;
    (starfieldSmall.material as any).uniforms.time.value = time;
  }
  if (starfieldLarge) {
    starfieldLarge.rotation.y += 0.0001;
    starfieldLarge.position.x += (mouseX * 0.01 - starfieldLarge.position.x) * 0.005;
    (starfieldLarge.material as any).uniforms.time.value = time;
  }

  renderer.render(scene, camera);
};

onMounted(() => {
    init();
    setTimeout(() => { onWindowResize(); }, 100);
});

onUnmounted(() => {
  cancelAnimationFrame(frameId);
  window.removeEventListener('resize', onWindowResize);
  document.removeEventListener('mousemove', onMouseMove);
  renderer?.dispose();
});
</script>
