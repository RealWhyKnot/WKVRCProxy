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
let nebulaMesh: THREE.Mesh | null = null;
let floatingParticles: THREE.Points | null = null;
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
    vec3 baseColor = color * 0.25;
    baseColor = mix(baseColor, glowColor * 0.5, fresnel);
    float rim = pow(1.0 - max(dot(viewDir, normal), 0.0), 8.0);
    baseColor += glowColor * rim * 1.2;
    float pulse = sin(time * 0.3) * 0.04 + 0.08;
    baseColor += glowColor * pulse;
    gl_FragColor = vec4(baseColor, 0.25 + fresnel * 0.5);
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

const nebulaVertexShader = `
  varying vec2 vUv;
  void main() {
    vUv = uv;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
  }
`;

const nebulaFragmentShader = `
  uniform float time;
  varying vec2 vUv;

  float noise(vec2 p) {
    return sin(p.x * 1.5 + time * 0.1) * cos(p.y * 1.3 - time * 0.08)
         + sin(p.x * 0.8 - time * 0.12 + p.y * 1.1) * 0.5
         + cos(p.x * 2.1 + p.y * 0.9 + time * 0.06) * 0.3;
  }

  void main() {
    vec2 uv = vUv - 0.5;
    float dist = length(uv);
    float n1 = noise(uv * 3.0);
    float n2 = noise(uv * 5.0 + 10.0);
    float n3 = noise(uv * 2.0 - 5.0);

    vec3 purple = vec3(0.3, 0.1, 0.5);
    vec3 blue = vec3(0.1, 0.2, 0.6);
    vec3 cyan = vec3(0.1, 0.4, 0.5);

    vec3 col = mix(purple, blue, smoothstep(-1.0, 1.0, n1));
    col = mix(col, cyan, smoothstep(-0.5, 1.0, n2) * 0.5);
    col += vec3(0.05, 0.08, 0.12) * smoothstep(-0.3, 1.0, n3);

    float fade = 1.0 - smoothstep(0.0, 0.5, dist);
    float alpha = fade * 0.12 * (0.5 + 0.5 * sin(time * 0.05 + n1));

    gl_FragColor = vec4(col, alpha);
  }
`;

const particleVertexShader = `
  attribute float size;
  attribute vec3 customColor;
  varying vec3 vColor;
  varying float vAlpha;
  uniform float time;
  void main() {
    vColor = customColor;
    float twinkle = sin(time * 1.5 + position.x * 0.01 + position.y * 0.01) * 0.5 + 0.5;
    vAlpha = 0.3 + twinkle * 0.4;
    vec4 mvPosition = modelViewMatrix * vec4(position, 1.0);
    gl_PointSize = size * (600.0 / -mvPosition.z);
    gl_Position = projectionMatrix * mvPosition;
  }
`;

const particleFragmentShader = `
  varying vec3 vColor;
  varying float vAlpha;
  void main() {
    float r = distance(gl_PointCoord, vec2(0.5));
    if (r > 0.5) discard;
    float glow = exp(-r * 3.0);
    gl_FragColor = vec4(vColor * glow, glow * vAlpha * 0.5);
  }
`;

const init = () => {
  if (!container.value) return;

  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0a0e1a);
  scene.fog = new THREE.FogExp2(0x0a0e1a, 0.0006);

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
      color: { value: new THREE.Color(0x0a1025) },
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

  // Nebula / aurora background plane
  const nebulaGeo = new THREE.PlaneGeometry(4000, 4000);
  const nebulaMat = new THREE.ShaderMaterial({
    uniforms: {
      time: { value: 0 }
    },
    vertexShader: nebulaVertexShader,
    fragmentShader: nebulaFragmentShader,
    transparent: true,
    blending: THREE.AdditiveBlending,
    depthWrite: false,
    side: THREE.DoubleSide
  });
  nebulaMesh = new THREE.Mesh(nebulaGeo, nebulaMat);
  nebulaMesh.position.z = -1500;
  scene.add(nebulaMesh);

  generateStarfield(3000, 4000, 2.0, 0.3, true);
  generateStarfield(800, 2000, 4.0, 0.8, false);

  // Floating particles
  {
    const particleCount = 200;
    const geo = new THREE.BufferGeometry();
    const pos = new Float32Array(particleCount * 3);
    const col = new Float32Array(particleCount * 3);
    const siz = new Float32Array(particleCount);

    for (let i = 0; i < particleCount; i++) {
      const i3 = i * 3;
      pos[i3] = (Math.random() - 0.5) * 3000;
      pos[i3 + 1] = (Math.random() - 0.5) * 3000;
      pos[i3 + 2] = (Math.random() - 0.5) * 2000;

      const color = new THREE.Color();
      const hue = 0.55 + Math.random() * 0.25; // blue, purple, cyan
      color.setHSL(hue, 0.4 + Math.random() * 0.4, 0.6 + Math.random() * 0.3);
      col[i3] = color.r; col[i3 + 1] = color.g; col[i3 + 2] = color.b;
      siz[i] = 5.0 + Math.random() * 5.0;
    }

    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('customColor', new THREE.BufferAttribute(col, 3));
    geo.setAttribute('size', new THREE.BufferAttribute(siz, 1));

    const mat = new THREE.ShaderMaterial({
      uniforms: { time: { value: 0 } },
      vertexShader: particleVertexShader,
      fragmentShader: particleFragmentShader,
      transparent: true,
      blending: THREE.AdditiveBlending,
      depthWrite: false
    });

    floatingParticles = new THREE.Points(geo, mat);
    scene.add(floatingParticles);
  }

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
    color.setHSL(0.5 + Math.random() * 0.3, 0.3 + Math.random() * 0.5, 0.7 + Math.random() * 0.3);
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

  if (nebulaMesh) {
    nebulaMesh.rotation.z += 0.00005;
    (nebulaMesh.material as any).uniforms.time.value = time;
  }

  if (floatingParticles) {
    (floatingParticles.material as any).uniforms.time.value = time;
    const positions = (floatingParticles.geometry as THREE.BufferGeometry).getAttribute('position') as THREE.BufferAttribute;
    for (let i = 0; i < positions.count; i++) {
      let y = positions.getY(i);
      y += 0.1;
      if (y > 1500) y = -1500;
      positions.setY(i, y);
    }
    positions.needsUpdate = true;
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
  if (nebulaMesh) {
    nebulaMesh.geometry.dispose();
    (nebulaMesh.material as THREE.ShaderMaterial).dispose();
    nebulaMesh = null;
  }
  if (floatingParticles) {
    floatingParticles.geometry.dispose();
    (floatingParticles.material as THREE.ShaderMaterial).dispose();
    floatingParticles = null;
  }
  renderer?.dispose();
});
</script>
