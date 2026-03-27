<script setup lang="ts">
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

    <div class="flex-grow bg-[#0a0a0c]/80 backdrop-blur-3xl border border-white/5 rounded-[32px] overflow-hidden flex flex-col shadow-2xl relative group">
      <div class="absolute inset-0 bg-gradient-to-b from-blue-500/5 to-transparent opacity-0 group-hover:opacity-100 transition-opacity duration-1000 pointer-events-none"></div>
      
      <div class="grid grid-cols-12 gap-4 px-8 py-5 border-b border-white/5 bg-black/40 backdrop-blur-md text-[9px] font-black uppercase tracking-[0.2em] text-white/30 sticky top-0 z-10 italic">
        <div class="col-span-2">Time</div>
        <div class="col-span-1">Method</div>
        <div class="col-span-6">Target</div>
        <div class="col-span-1 text-center">Status</div>
        <div class="col-span-2 text-right">Transferred</div>
      </div>

      <div class="flex-grow overflow-y-auto no-scrollbar relative z-0">
        <div v-if="appStore.relayEvents.length === 0" class="h-full flex flex-col items-center justify-center text-white/20">
          <i class="bi bi-activity text-4xl mb-4 opacity-50"></i>
          <p class="text-[10px] font-black uppercase tracking-widest italic">Awaiting traffic...</p>
        </div>

        <div v-else class="flex flex-col">
          <div v-for="evt in appStore.relayEvents" :key="evt.id" 
               class="grid grid-cols-12 gap-4 px-8 py-4 border-b border-white/5 hover:bg-white/[0.02] transition-colors items-center">
            
            <!-- Time -->
            <div class="col-span-2 font-mono text-[10px] text-white/50">
              {{ new Date(evt.timestamp).toLocaleTimeString() }}
            </div>
            
            <!-- Method -->
            <div class="col-span-1">
              <span class="px-2 py-1 rounded text-[8px] font-black uppercase tracking-widest bg-white/5 text-blue-300 border border-white/10">
                {{ evt.method }}
              </span>
            </div>

            <!-- Target URL -->
            <div class="col-span-6 truncate font-mono text-[10px] text-white/70" :title="evt.targetUrl">
              {{ evt.targetUrl }}
            </div>

            <!-- Status -->
            <div class="col-span-1 flex justify-center">
              <div v-if="evt.statusCode === 0" class="w-2 h-2 rounded-full bg-blue-500 shadow-[0_0_10px_rgba(59,130,246,0.8)] animate-pulse" title="Pending"></div>
              <span v-else-if="evt.statusCode >= 200 && evt.statusCode < 300" class="text-emerald-400 font-black text-[10px]" title="Success">{{ evt.statusCode }}</span>
              <span v-else-if="evt.statusCode >= 300 && evt.statusCode < 400" class="text-orange-400 font-black text-[10px]">{{ evt.statusCode }}</span>
              <span v-else class="text-red-400 font-black text-[10px]" title="Error">{{ evt.statusCode }}</span>
            </div>

            <!-- Transferred -->
            <div class="col-span-2 text-right font-mono text-[10px] text-white/50">
              {{ formatBytes(evt.bytesTransferred) }}
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
