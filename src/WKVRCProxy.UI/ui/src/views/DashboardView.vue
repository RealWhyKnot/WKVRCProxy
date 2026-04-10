<script setup lang="ts">
import { computed } from 'vue'
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

const tierDisplay: Record<string, { short: string, color: string }> = {
  'tier1': { short: 'Local', color: 'bg-blue-500' },
  'tier2': { short: 'Cloud', color: 'bg-purple-500' },
  'tier3': { short: 'Fallback', color: 'bg-amber-500' },
  'tier4': { short: 'Direct', color: 'bg-white/20' }
}

const totalResolutions = computed(() => {
  return Object.values(appStore.status.stats.tierStats).reduce((a, b) => a + b, 0)
})

const tierPercentages = computed(() => {
  const total = totalResolutions.value
  if (total === 0) return {}
  const res: Record<string, number> = {}
  for (const [tier, count] of Object.entries(appStore.status.stats.tierStats)) {
    res[tier] = (count / total) * 100
  }
  return res
})

const recentHistory = computed(() => appStore.config.history.slice(0, 5))
</script>

<template>
  <div class="p-8 space-y-8 animate-in fade-in duration-700">
    <!-- Header with Live Activity -->
    <div class="flex justify-between items-end">
      <div class="space-y-2">
        <h2 class="text-3xl font-black uppercase tracking-tighter italic">Dashboard</h2>
        <div class="flex items-center gap-2 ml-1">
          <div class="flex items-center gap-1.5 px-2.5 py-1 bg-blue-500/10 border border-blue-500/20 rounded-full">
            <span class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-pulse"></span>
            <span class="text-[10px] font-black uppercase tracking-widest text-blue-400 italic">{{ appStore.status.message }}</span>
          </div>
          <p class="text-white/45 font-black uppercase tracking-[0.3em] text-[9px]">Live status</p>
        </div>
      </div>

      <div class="flex gap-6 items-center bg-white/[0.02] border border-white/5 px-6 py-4 rounded-2xl backdrop-blur-xl">
        <div class="text-center">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Resolving</p>
          <p class="text-lg font-black italic">{{ appStore.status.stats.activeCount }}</p>
        </div>
        <div class="w-[1px] h-6 bg-white/10"></div>
        <div class="text-center">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Player</p>
          <p class="text-lg font-black italic text-blue-400">{{ appStore.status.stats.player }}</p>
        </div>
        <div class="w-[1px] h-6 bg-white/10"></div>
        <div class="text-center">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Cloud</p>
          <p class="text-lg font-black italic text-purple-400 uppercase">{{ appStore.status.stats.node.split('.')[0] }}</p>
        </div>
      </div>
    </div>

    <!-- Tier Usage Chart -->
    <div class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-8 relative overflow-hidden group">
      <div class="absolute -top-20 -right-20 w-64 h-64 bg-blue-500/5 blur-[100px] rounded-full group-hover:bg-blue-500/10 transition-all duration-1000"></div>

      <div class="flex justify-between items-center relative z-10">
        <div class="space-y-1">
          <h3 class="text-xl font-black uppercase tracking-tighter italic">Resolution Stats</h3>
          <p class="text-[9px] text-white/45 font-black uppercase tracking-widest">Requests handled per extraction tier</p>
        </div>
        <div class="text-right">
          <p class="text-3xl font-black italic text-white/90">{{ totalResolutions }}</p>
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45">Total Resolved</p>
        </div>
      </div>

      <div class="space-y-6 relative z-10">
        <div class="h-4 w-full bg-white/5 rounded-full overflow-hidden flex border border-white/5">
          <div v-for="(pct, tier) in tierPercentages" :key="tier"
               :class="[tierDisplay[tier]?.color, 'h-full transition-all duration-1000 ease-out border-r border-black/20 last:border-0']"
               :style="{ width: pct + '%' }">
          </div>
        </div>

        <div class="grid grid-cols-2 md:grid-cols-4 gap-4">
          <div v-for="(data, tier) in tierDisplay" :key="tier" class="space-y-1 group/tier bg-white/[0.01] p-4 rounded-2xl border border-transparent hover:border-white/10 transition-all">
            <div class="flex items-center gap-2">
              <div :class="[data.color, 'w-1.5 h-1.5 rounded-full']"></div>
              <span class="text-[10px] font-black uppercase tracking-widest text-white/55 italic group-hover/tier:text-white transition-colors">{{ data.short }}</span>
            </div>
            <div class="flex items-baseline gap-2">
              <p class="text-lg font-black italic text-white/90">{{ appStore.status.stats.tierStats[tier] || 0 }}</p>
              <p class="text-[9px] font-black uppercase tracking-widest text-white/35">{{ Math.round(tierPercentages[tier] || 0) }}%</p>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Recent History & Logs -->
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-8">
      <div class="bg-white/[0.02] border border-white/5 rounded-[32px] p-8 space-y-6 backdrop-blur-3xl group">
        <div class="flex justify-between items-center">
          <h3 class="text-lg font-black uppercase tracking-tighter italic">Recent Activity</h3>
          <button @click="appStore.activeTab = 'history'" class="text-[9px] font-black uppercase tracking-widest text-blue-400 hover:text-blue-300 transition-colors italic">View History</button>
        </div>

        <div class="space-y-3">
          <div v-for="(entry, i) in recentHistory" :key="i" class="flex items-center gap-4 p-4 bg-white/[0.02] border border-white/5 rounded-2xl hover:bg-white/[0.04] transition-all group/item">
            <div :class="[entry.Success ? 'bg-emerald-500/10 text-emerald-400' : 'bg-red-500/10 text-red-400', 'w-8 h-8 rounded-xl flex items-center justify-center shrink-0 border border-current/10']">
              <i :class="[entry.Success ? 'bi-check-lg' : 'bi-exclamation-triangle', 'text-sm']"></i>
            </div>
            <div class="min-w-0 flex-grow">
              <p class="text-[11px] font-black text-white/80 truncate italic group-hover/item:text-blue-400 transition-colors">{{ entry.OriginalUrl }}</p>
              <div class="flex items-center gap-2 mt-1">
                <span class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">{{ tierDisplay[entry.Tier.split('-')[0]]?.short || entry.Tier }}</span>
                <span class="w-0.5 h-0.5 bg-white/20 rounded-full"></span>
                <span class="text-[8px] font-bold text-white/45 uppercase tabular-nums tracking-widest">{{ new Date(entry.Timestamp).toLocaleTimeString() }}</span>
              </div>
            </div>
            <div class="flex flex-col gap-1 items-end shrink-0">
              <span class="px-2 py-0.5 bg-white/5 rounded-lg text-[8px] font-black text-white/50 uppercase italic border border-white/5">{{ entry.Player }}</span>
              <span v-if="entry.IsLive" class="px-2 py-0.5 bg-green-500/20 rounded-lg text-[8px] font-black text-green-400 uppercase border border-green-500/30 flex items-center gap-1">
                <span class="w-1 h-1 bg-green-400 rounded-full animate-pulse inline-block"></span>LIVE
              </span>
              <span v-else class="px-2 py-0.5 bg-white/5 rounded-lg text-[8px] font-black text-white/35 uppercase border border-white/5">VOD</span>
            </div>
          </div>
          <div v-if="recentHistory.length === 0" class="py-10 text-center uppercase tracking-[0.4em] text-white/25 text-[9px] font-black italic">No activity yet</div>
        </div>
      </div>

      <div class="bg-white/[0.02] border border-white/5 rounded-[32px] p-8 space-y-6 backdrop-blur-3xl group">
        <div class="flex justify-between items-center">
          <h3 class="text-lg font-black uppercase tracking-tighter italic">Recent Logs</h3>
          <button @click="appStore.activeTab = 'logs'" class="text-[9px] font-black uppercase tracking-widest text-white/45 hover:text-white/70 transition-colors italic">Full Logs</button>
        </div>

        <div class="space-y-2 font-mono text-[9px]">
          <div v-for="(log, i) in appStore.logs.slice(-10).reverse()" :key="i" class="flex gap-3 px-3 py-1.5 border-l-2 border-white/5 hover:border-blue-500/40 hover:bg-white/[0.02] transition-all">
            <span class="text-white/35 shrink-0 font-bold tabular-nums">{{ log.Timestamp.split('T')[1]?.split('.')[0] }}</span>
            <span class="text-white/65 break-all leading-normal">{{ log.Message }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
