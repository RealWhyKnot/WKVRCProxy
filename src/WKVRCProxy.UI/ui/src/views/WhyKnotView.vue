<script setup lang="ts">
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

function openBrowser() {
  appStore.sendMessage('OPEN_BROWSER', { url: 'https://whyknot.dev' })
}
</script>

<template>
  <div class="h-full flex flex-col items-center justify-center p-8 animate-in fade-in duration-700">
    <div class="max-w-md w-full bg-white/[0.03] border border-white/5 p-12 rounded-[40px] text-center space-y-8 backdrop-blur-3xl shadow-2xl relative overflow-hidden group">
      <div class="absolute -top-20 -right-20 w-64 h-64 bg-blue-500/5 blur-[100px] rounded-full group-hover:bg-blue-500/10 transition-all"></div>
      
      <div class="w-20 h-20 bg-blue-500/10 rounded-3xl flex items-center justify-center text-blue-500 mx-auto shadow-lg shadow-blue-500/10 group-hover:scale-110 transition-transform">
        <i class="bi bi-globe text-4xl"></i>
      </div>

      <div class="space-y-3">
        <h2 class="text-3xl font-black uppercase tracking-tighter italic text-white/90">WhyKnot.dev</h2>
        <p class="text-white/55 text-[10px] font-bold uppercase tracking-[0.2em] leading-relaxed">
          Cloud backup resolution service. Opens in your browser.
        </p>
      </div>

      <button @click="openBrowser" class="w-full py-5 bg-blue-600 hover:bg-blue-500 text-white rounded-3xl font-black text-[10px] uppercase tracking-[0.3em] transition-all italic active:scale-95 shadow-xl shadow-blue-600/20 group/btn">
        <i class="bi bi-box-arrow-up-right mr-2 group-hover/btn:translate-x-1 group-hover/btn:-translate-y-1 transition-transform inline-block"></i>
        Launch Website
      </button>
    </div>
  </div>
</template>