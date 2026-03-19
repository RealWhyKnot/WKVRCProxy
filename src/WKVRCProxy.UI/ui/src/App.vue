<script setup lang="ts">
import { onMounted } from 'vue'
import { useAppStore } from './stores/appStore'
import ThreeBackground from './components/ThreeBackground.vue'
import Sidebar from './components/layout/Sidebar.vue'
import DashboardView from './views/DashboardView.vue'
import HistoryView from './views/HistoryView.vue'
import WhyKnotView from './views/WhyKnotView.vue'
import LogsView from './views/LogsView.vue'
import SettingsView from './views/SettingsView.vue'

const appStore = useAppStore()

onMounted(() => {
  if (!appStore.initBridge()) {
    window.addEventListener('photino-ready', () => {
      appStore.initBridge();
    });
  }
})
</script>

<template>
  <div class="h-screen w-screen flex overflow-hidden bg-[#010103] text-white font-sans selection:bg-blue-500/30 antialiased">
    <!-- Critical Failure Overlay -->
    <div v-if="!appStore.isBridgeReady" class="fixed inset-0 z-[100] bg-black flex items-center justify-center backdrop-blur-3xl animate-in fade-in duration-500">
      <div class="text-center space-y-6 max-w-md p-10">
        <div class="w-16 h-16 bg-red-500/20 border border-red-500/40 rounded-3xl flex items-center justify-center mx-auto mb-6 animate-pulse shadow-2xl shadow-red-500/20">
          <i class="bi bi-exclamation-triangle-fill text-red-500 text-3xl"></i>
        </div>
        <h1 class="text-2xl font-black uppercase tracking-tighter italic">Link Failure</h1>
        <p class="text-white/40 text-[9px] font-bold leading-relaxed uppercase tracking-[0.2em]">Unable to connect to system core.</p>
        <div class="flex justify-center gap-2">
          <div class="w-1 h-1 bg-red-500 rounded-full animate-bounce"></div>
          <div class="w-1 h-1 bg-red-500 rounded-full animate-bounce [animation-delay:0.2s]"></div>
          <div class="w-1 h-1 bg-red-500 rounded-full animate-bounce [animation-delay:0.4s]"></div>
        </div>
      </div>
    </div>

    <!-- 3D Background & Overlays -->
    <ThreeBackground :isReduced="appStore.activeTab !== 'dashboard'" />
    <div class="fixed inset-0 z-[1] pointer-events-none bg-[radial-gradient(circle_at_center,transparent_0%,#010103_85%)] opacity-90"></div>
    <div class="fixed inset-0 z-[2] pointer-events-none bg-[url('https://grainy-gradients.vercel.app/noise.svg')] opacity-[0.03] mix-blend-overlay"></div>

    <!-- Sidebar -->
    <Sidebar class="z-20" />

    <!-- Main Content -->
    <main class="flex-grow flex flex-col relative z-10 h-full overflow-hidden">
      <div class="flex-grow overflow-y-auto no-scrollbar">
        <transition mode="out-in"
                    enter-active-class="transition duration-500 ease-out"
                    enter-from-class="opacity-0 translate-y-4 scale-[0.98]"
                    enter-to-class="opacity-100 translate-y-0 scale-100"
                    leave-active-class="transition duration-200 ease-in"
                    leave-from-class="opacity-100 scale-100"
                    leave-to-class="opacity-0 scale-[1.02]">
          <div :key="appStore.activeTab" class="h-full">
            <DashboardView v-if="appStore.activeTab === 'dashboard'" />
            <HistoryView v-if="appStore.activeTab === 'history'" />
            <WhyKnotView v-if="appStore.activeTab === 'whyknot'" />
            <LogsView v-if="appStore.activeTab === 'logs'" />
            <SettingsView v-if="appStore.activeTab === 'settings'" />
          </div>
        </transition>
      </div>

      <!-- Footer Area -->
      <footer class="px-8 py-6 border-t border-white/5 bg-black/20 backdrop-blur-xl flex items-center justify-between z-20">
        <div class="flex items-center gap-3 text-[8px] font-bold text-white/20 uppercase tracking-[0.2em]">
          <span>&copy; {{ new Date().getFullYear() }} WhyKnot</span>
        </div>
        
        <div class="flex items-center gap-8 font-mono text-[8px] uppercase tracking-widest text-white/20">
          <div class="flex items-center gap-2">
            <span class="w-1 h-1 bg-blue-500/40 rounded-full"></span>
            Build: <span class="text-white/40">{{ appStore.version }}</span>
          </div>
          <div class="flex items-center gap-2">
            <span class="w-1 h-1 bg-emerald-500/40 rounded-full"></span>
            Cloud: <span class="text-white/40">WhyKnot.dev</span>
          </div>
        </div>
      </footer>
    </main>
  </div>
</template>

<style>
.no-scrollbar::-webkit-scrollbar { display: none; }
::-webkit-scrollbar { width: 4px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb { background: rgba(255,255,255,0.1); border-radius: 10px; }
::-webkit-scrollbar-thumb:hover { background: rgba(255,255,255,0.2); }

@keyframes fade-in { from { opacity: 0; } to { opacity: 1; } }
@keyframes slide-in-from-bottom-4 { from { transform: translateY(1rem); opacity: 0; } to { transform: translateY(0); opacity: 1; } }
@keyframes zoom-in-95 { from { transform: scale(0.95); opacity: 0; } to { transform: scale(1); opacity: 1; } }
.animate-in { animation-fill-mode: both; }
</style>