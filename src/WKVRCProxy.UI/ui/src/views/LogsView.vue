<script setup lang="ts">
import { ref, watch, nextTick, onMounted } from 'vue'
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()
const scrollContainer = ref<HTMLElement | null>(null)

const logLevelNames = ['Trace', 'Debug', 'Info', 'Success', 'Warning', 'Error', 'Fatal']
const logLevelClasses = [
  'text-white/10',
  'text-blue-500/50',
  'text-white/40',
  'text-emerald-500 font-black italic',
  'text-yellow-500/50',
  'text-red-500/60',
  'text-red-600 font-black italic'
]

const scrollToBottom = () => {
  if (scrollContainer.value) {
    scrollContainer.value.scrollTop = scrollContainer.value.scrollHeight
  }
}

watch(() => appStore.logs.length, () => {
  nextTick(() => scrollToBottom())
})

onMounted(() => {
  nextTick(() => scrollToBottom())
})
</script>

<template>
  <div class="p-8 h-full flex flex-col space-y-8">
    <div class="space-y-2 shrink-0">
      <h2 class="text-3xl font-black uppercase tracking-tighter italic">System <span class="text-blue-500">Logs</span></h2>
      <p class="text-white/20 font-black uppercase tracking-[0.4em] text-[8px] ml-1">Event stream</p>
    </div>

    <div class="flex-grow bg-white/[0.02] border border-white/5 rounded-[32px] overflow-hidden backdrop-blur-3xl shadow-2xl flex flex-col font-mono text-[9px]">
      <div class="px-8 py-4 bg-white/[0.01] border-b border-white/5 flex justify-between text-white/10 font-black uppercase tracking-[0.3em] italic">
        <span>History</span>
        <span>{{ appStore.logs.length }} Events</span>
      </div>
      <div ref="scrollContainer" class="flex-grow overflow-y-auto p-8 space-y-1.5 no-scrollbar leading-relaxed scroll-smooth group">
        <div v-for="(log, i) in appStore.logs" :key="i" class="flex gap-4 border-l border-transparent hover:border-blue-500/40 hover:bg-white/[0.03] px-4 transition-all py-0.5 rounded-r-lg group/item">
          <span class="text-white/10 shrink-0 group-hover/item:text-white/30 font-bold tabular-nums">{{ log.Timestamp.split('T')[1]?.split('.')[0] }}</span>
          <span :class="logLevelClasses[log.Level]" class="shrink-0 w-20 uppercase tracking-widest">[{{ logLevelNames[log.Level] }}]</span>
          <span class="text-white/40 group-hover/item:text-white/80 transition-colors break-all">{{ log.Message }}</span>
        </div>
        <div v-if="appStore.logs.length === 0" class="text-white/5 italic py-20 text-center uppercase tracking-[0.8em] font-black">Idle</div>
      </div>
    </div>
  </div>
</template>