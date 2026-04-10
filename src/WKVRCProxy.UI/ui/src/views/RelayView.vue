<script setup lang="ts">
import { computed } from 'vue'
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

const clearEvents = () => {
  appStore.relayEvents = []
}

const formatBytes = (bytes: number) => {
  if (bytes === 0) return '0 B'
  const k = 1024
  const sizes = ['B', 'KB', 'MB', 'GB']
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i]
}

const totalRequests = computed(() => appStore.relayEvents.length)

const successRate = computed(() => {
  const total = appStore.relayEvents.length
  if (total === 0) return '0'
  const successes = appStore.relayEvents.filter(e => e.statusCode >= 200 && e.statusCode < 300).length
  return ((successes / total) * 100).toFixed(1)
})

const totalTransferred = computed(() => {
  const sum = appStore.relayEvents.reduce((acc, e) => acc + (e.bytesTransferred || 0), 0)
  return formatBytes(sum)
})

const activeCount = computed(() => appStore.relayEvents.filter(e => e.statusCode === 0).length)

const methodClasses = (method: string) => {
  switch (method?.toUpperCase()) {
    case 'GET':
      return 'bg-blue-500/10 text-blue-300 border-blue-500/20'
    case 'POST':
      return 'bg-purple-500/10 text-purple-300 border-purple-500/20'
    default:
      return 'bg-white/5 text-white/70 border-white/10'
  }
}
</script>

<template>
  <div class="h-full flex flex-col p-8 lg:p-12 max-w-7xl mx-auto w-full animate-in fade-in zoom-in-95 duration-700">
    <div class="flex justify-between items-end mb-12">
      <div>
        <h2 class="text-4xl font-black uppercase tracking-tighter mb-2 italic">Traffic <span class="text-blue-500">Monitor</span></h2>
        <p class="text-xs text-white/40 font-bold tracking-[0.2em] uppercase">Live localhost relay telemetry</p>
      </div>

      <button @click="clearEvents" class="bg-white/5 border border-white/10 hover:bg-white/10 hover:border-blue-500/30 text-white/50 hover:text-white px-6 py-2.5 rounded-xl transition-all duration-300 font-black text-[10px] uppercase tracking-widest flex items-center gap-2 group backdrop-blur-xl italic">
        <i class="bi bi-trash-fill text-blue-500 group-hover:scale-110 transition-transform"></i>
        Clear
      </button>
    </div>

    <div class="grid grid-cols-4 gap-4 mb-8">
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl">
        <div class="text-2xl font-black italic">{{ totalRequests }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">Total Requests</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl">
        <div class="text-2xl font-black italic">{{ successRate }}<span class="text-sm text-white/40">%</span></div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">Success Rate</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl">
        <div class="text-2xl font-black italic">{{ totalTransferred }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">Total Transferred</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl">
        <div class="text-2xl font-black italic">{{ activeCount }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">Active</div>
      </div>
    </div>

    <div class="flex-grow bg-[#0a0a0c]/80 backdrop-blur-3xl border border-white/5 rounded-[32px] overflow-hidden flex flex-col shadow-2xl relative group">
      <div class="absolute inset-0 bg-gradient-to-b from-blue-500/5 to-transparent opacity-0 group-hover:opacity-100 transition-opacity duration-1000 pointer-events-none"></div>
      
      <div class="grid grid-cols-12 gap-4 px-8 py-5 border-b border-white/5 bg-black/40 backdrop-blur-md text-[9px] font-black uppercase tracking-[0.2em] text-white/55 sticky top-0 z-10 italic">
        <div class="col-span-2">Time</div>
        <div class="col-span-1">Method</div>
        <div class="col-span-6">Target</div>
        <div class="col-span-1 text-center">Status</div>
        <div class="col-span-2 text-right">Transferred</div>
      </div>

      <div class="flex-grow overflow-y-auto no-scrollbar relative z-0">
        <div v-if="appStore.relayEvents.length === 0" class="h-full flex flex-col items-center justify-center text-white/20 py-24">
          <div class="relative mb-6">
            <i class="bi bi-activity text-6xl opacity-30 animate-pulse"></i>
            <div class="absolute inset-0 bg-blue-500/10 blur-3xl rounded-full"></div>
          </div>
          <p class="text-xs font-black uppercase tracking-widest italic mb-2">Awaiting traffic...</p>
          <p class="text-[10px] text-white/25 font-medium max-w-xs text-center leading-relaxed">Relay requests will appear here in real time as they pass through the proxy.</p>
        </div>

        <div v-else class="flex flex-col">
          <div v-for="evt in appStore.relayEvents" :key="evt.id"
               class="grid grid-cols-12 gap-4 px-8 py-4 border-b border-white/5 hover:bg-white/[0.02] transition-colors items-center animate-[slideIn_0.3s_ease-out]">
            
            <!-- Time -->
            <div class="col-span-2 font-mono text-[10px] text-white/65">
              {{ new Date(evt.timestamp).toLocaleTimeString() }}
            </div>
            
            <!-- Method -->
            <div class="col-span-1">
              <span class="px-2 py-1 rounded text-[8px] font-black uppercase tracking-widest border" :class="methodClasses(evt.method)">
                {{ evt.method }}
              </span>
            </div>

            <!-- Target URL -->
            <div class="col-span-6 truncate font-mono text-[10px] text-white/85" :title="evt.targetUrl">
              {{ evt.targetUrl }}
            </div>

            <!-- Status -->
            <div class="col-span-1 flex justify-center">
              <div v-if="evt.statusCode === 0" class="w-2 h-2 rounded-full bg-blue-500 shadow-[0_0_10px_rgba(59,130,246,0.8)] animate-pulse" title="Pending"></div>
              <span v-else-if="evt.statusCode >= 200 && evt.statusCode < 300" class="bg-emerald-500/10 text-emerald-400 px-2 py-0.5 rounded-lg font-black text-[10px]" title="Success">{{ evt.statusCode }}</span>
              <span v-else-if="evt.statusCode >= 300 && evt.statusCode < 400" class="bg-orange-500/10 text-orange-400 px-2 py-0.5 rounded-lg font-black text-[10px]">{{ evt.statusCode }}</span>
              <span v-else class="bg-red-500/10 text-red-400 px-2 py-0.5 rounded-lg font-black text-[10px]" title="Error">{{ evt.statusCode }}</span>
            </div>

            <!-- Transferred -->
            <div class="col-span-2 text-right font-mono text-[10px] text-white/65">
              {{ formatBytes(evt.bytesTransferred) }}
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
@keyframes slideIn {
  from {
    opacity: 0;
    transform: translateY(-8px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}
</style>
