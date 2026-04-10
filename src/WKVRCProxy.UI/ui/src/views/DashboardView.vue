<script setup lang="ts">
import { computed, ref, watch, onMounted, onUnmounted } from 'vue'
import { useAppStore, TIER_DISPLAY } from '../stores/appStore'

const appStore = useAppStore()

// --- Composable: useAnimatedNumber ---
function useAnimatedNumber(source: () => number, duration = 600) {
  const display = ref(0)
  let animId = 0

  function animate(from: number, to: number) {
    cancelAnimationFrame(animId)
    const start = performance.now()
    const step = (now: number) => {
      const elapsed = Math.min((now - start) / duration, 1)
      // ease-out cubic
      const t = 1 - Math.pow(1 - elapsed, 3)
      display.value = Math.round(from + (to - from) * t)
      if (elapsed < 1) {
        animId = requestAnimationFrame(step)
      }
    }
    animId = requestAnimationFrame(step)
  }

  watch(source, (newVal, oldVal) => {
    animate(oldVal ?? 0, newVal)
  }, { immediate: true })

  onUnmounted(() => cancelAnimationFrame(animId))
  return display
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

// --- Animated counters ---
const animatedTotal = useAnimatedNumber(() => totalResolutions.value)
const animatedTier1 = useAnimatedNumber(() => appStore.status.stats.tierStats['tier1'] ?? 0)
const animatedTier2 = useAnimatedNumber(() => appStore.status.stats.tierStats['tier2'] ?? 0)
const animatedTier3 = useAnimatedNumber(() => appStore.status.stats.tierStats['tier3'] ?? 0)
const animatedTier4 = useAnimatedNumber(() => appStore.status.stats.tierStats['tier4'] ?? 0)
const animatedTierValues: Record<string, ReturnType<typeof useAnimatedNumber>> = {
  tier1: animatedTier1,
  tier2: animatedTier2,
  tier3: animatedTier3,
  tier4: animatedTier4
}

// --- Success rate (from store) ---
const animatedSuccessRate = useAnimatedNumber(() => appStore.successRate)

// SVG ring constants
const ringRadius = 46
const ringCircumference = 2 * Math.PI * ringRadius
const successDash = computed(() => (appStore.successRate / 100) * ringCircumference)

// --- Sparkline ---
const sparklinePoints = computed(() => {
  const entries = appStore.config.history.slice(0, 20).reverse()
  if (entries.length === 0) return ''
  const width = 200
  const height = 40
  const padding = 4
  const usableH = height - padding * 2
  const stepX = entries.length > 1 ? (width - padding * 2) / (entries.length - 1) : 0
  return entries.map((e, i) => {
    const x = padding + i * stepX
    const y = e.Success ? padding : padding + usableH
    return `${x},${y}`
  }).join(' ')
})

// --- Uptime ---
const uptime = ref('00:00:00')
let uptimeStart = 0
let uptimeInterval = 0

function formatUptime(ms: number): string {
  const totalSec = Math.floor(ms / 1000)
  const h = String(Math.floor(totalSec / 3600)).padStart(2, '0')
  const m = String(Math.floor((totalSec % 3600) / 60)).padStart(2, '0')
  const s = String(totalSec % 60).padStart(2, '0')
  return `${h}:${m}:${s}`
}

onMounted(() => {
  uptimeStart = Date.now()
  uptimeInterval = window.setInterval(() => {
    uptime.value = formatUptime(Date.now() - uptimeStart)
  }, 1000)
})
onUnmounted(() => {
  clearInterval(uptimeInterval)
})

// --- Active pulse ---
const isResolving = computed(() => appStore.status.stats.activeCount > 0)
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

      <div class="flex gap-6 items-center bg-white/[0.02] border border-white/5 px-6 py-4 rounded-2xl backdrop-blur-xl transition-shadow duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)]">
        <div class="text-center relative">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Resolving</p>
          <p class="text-lg font-black italic relative z-10">{{ appStore.status.stats.activeCount }}</p>
          <span v-if="isResolving" class="absolute inset-0 rounded-xl bg-blue-500/20 animate-[pulse-glow_2s_ease-in-out_infinite] blur-md"></span>
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
        <div class="w-[1px] h-6 bg-white/10"></div>
        <div class="text-center">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Uptime</p>
          <p class="text-lg font-black italic text-emerald-400 tabular-nums">{{ uptime }}</p>
        </div>
      </div>
    </div>

    <!-- Tier Usage Chart -->
    <div class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-8 relative overflow-hidden group transition-shadow duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)]">
      <div class="absolute -top-20 -right-20 w-64 h-64 bg-blue-500/5 blur-[100px] rounded-full group-hover:bg-blue-500/10 transition-all duration-1000"></div>

      <div class="flex justify-between items-center relative z-10">
        <div class="space-y-1">
          <h3 class="text-xl font-black uppercase tracking-tighter italic">Resolution Stats</h3>
          <p class="text-[9px] text-white/45 font-black uppercase tracking-widest">Requests handled per extraction tier</p>
        </div>
        <div class="text-right">
          <p class="text-3xl font-black italic text-white/90">{{ animatedTotal }}</p>
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45">Total Resolved</p>
        </div>
      </div>

      <div class="space-y-6 relative z-10">
        <template v-if="totalResolutions > 0">
          <div class="h-4 w-full bg-white/5 rounded-full overflow-hidden flex border border-white/5">
            <div v-for="(pct, tier) in tierPercentages" :key="tier"
                 :class="[TIER_DISPLAY[tier]?.color, 'h-full transition-all duration-1000 ease-out border-r border-black/20 last:border-0']"
                 :style="{ width: pct + '%' }">
            </div>
          </div>

          <div class="grid grid-cols-2 md:grid-cols-4 gap-4">
            <div v-for="(data, tier) in TIER_DISPLAY" :key="tier" class="space-y-1 group/tier bg-white/[0.01] p-4 rounded-2xl border border-transparent hover:border-white/10 transition-all transition-shadow duration-500 hover:shadow-[0_0_20px_rgba(59,130,246,0.07)]">
              <div class="flex items-center gap-2">
                <div :class="[data.color, 'w-1.5 h-1.5 rounded-full']"></div>
                <span class="text-[10px] font-black uppercase tracking-widest text-white/55 italic group-hover/tier:text-white transition-colors">{{ data.short }}</span>
              </div>
              <div class="flex items-baseline gap-2">
                <p class="text-lg font-black italic text-white/90">{{ animatedTierValues[tier]?.value ?? 0 }}</p>
                <p class="text-[9px] font-black uppercase tracking-widest text-white/35">{{ Math.round(tierPercentages[tier] || 0) }}%</p>
              </div>
            </div>
          </div>
        </template>
        <div v-else class="py-6 text-center uppercase tracking-[0.4em] text-white/20 text-[9px] font-black italic">No resolutions yet</div>

        <!-- Success Rate Donut -->
        <div class="flex items-center gap-6 pt-2">
          <div class="relative w-[120px] h-[120px] shrink-0">
            <svg viewBox="0 0 120 120" class="w-full h-full -rotate-90">
              <!-- Background ring -->
              <circle cx="60" cy="60" :r="ringRadius" fill="none" stroke="rgba(255,255,255,0.06)" stroke-width="10" />
              <!-- Failure portion (full ring, behind success) -->
              <circle cx="60" cy="60" :r="ringRadius" fill="none" stroke="rgba(239,68,68,0.35)" stroke-width="10"
                      :stroke-dasharray="ringCircumference"
                      stroke-linecap="round" />
              <!-- Success portion -->
              <circle cx="60" cy="60" :r="ringRadius" fill="none" stroke="rgb(34,197,94)" stroke-width="10"
                      :stroke-dasharray="`${successDash} ${ringCircumference}`"
                      stroke-linecap="round"
                      style="transition: stroke-dasharray 1s ease-out" />
            </svg>
            <!-- Center text -->
            <div class="absolute inset-0 flex flex-col items-center justify-center rotate-0">
              <span class="text-2xl font-black italic text-white/90 tabular-nums">{{ animatedSuccessRate }}%</span>
            </div>
          </div>
          <div class="space-y-1">
            <p class="text-sm font-black uppercase tracking-tighter italic text-white/80">Success Rate</p>
            <p class="text-[9px] text-white/40 font-black uppercase tracking-widest">Based on {{ appStore.config.history.length }} {{ appStore.config.history.length === 1 ? 'request' : 'requests' }}</p>
            <div class="flex items-center gap-3 mt-2">
              <div class="flex items-center gap-1.5">
                <span class="w-2 h-2 rounded-full bg-green-500"></span>
                <span class="text-[9px] font-black text-white/50 uppercase tracking-widest">Success</span>
              </div>
              <div class="flex items-center gap-1.5">
                <span class="w-2 h-2 rounded-full bg-red-500/50"></span>
                <span class="text-[9px] font-black text-white/50 uppercase tracking-widest">Failed</span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Recent History & Logs -->
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-8">
      <div class="bg-white/[0.02] border border-white/5 rounded-[32px] p-8 space-y-6 backdrop-blur-3xl group transition-shadow duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)]">
        <div class="flex justify-between items-center">
          <h3 class="text-lg font-black uppercase tracking-tighter italic">Recent Activity</h3>
          <button @click="appStore.activeTab = 'history'" class="text-[9px] font-black uppercase tracking-widest text-blue-400 hover:text-blue-300 transition-colors italic">View History</button>
        </div>

        <!-- Sparkline -->
        <div v-if="appStore.config.history.length > 1" class="px-1">
          <svg width="200" height="40" viewBox="0 0 200 40" class="w-full" preserveAspectRatio="none">
            <defs>
              <linearGradient id="sparkGrad" x1="0" y1="0" x2="1" y2="0">
                <stop offset="0%" stop-color="rgb(59,130,246)" />
                <stop offset="100%" stop-color="rgb(34,211,238)" />
              </linearGradient>
            </defs>
            <polyline
              :points="sparklinePoints"
              fill="none"
              stroke="url(#sparkGrad)"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
            <!-- Dots on each point -->
            <circle v-for="(entry, i) in appStore.config.history.slice(0, 20).reverse()" :key="'dot-' + i"
              :cx="appStore.config.history.slice(0, 20).length > 1 ? 4 + i * ((200 - 8) / (Math.min(appStore.config.history.length, 20) - 1)) : 100"
              :cy="entry.Success ? 4 : 36"
              r="2.5"
              :fill="entry.Success ? 'rgb(34,211,238)' : 'rgb(239,68,68)'"
              opacity="0.7"
            />
          </svg>
          <div class="flex justify-between mt-1">
            <span class="text-[7px] font-black uppercase tracking-widest text-white/25">Older</span>
            <span class="text-[7px] font-black uppercase tracking-widest text-white/25">Recent</span>
          </div>
        </div>

        <div class="space-y-3">
          <div v-for="(entry, i) in recentHistory" :key="i" class="flex items-center gap-4 p-4 bg-white/[0.02] border border-white/5 rounded-2xl hover:bg-white/[0.04] transition-all group/item">
            <div :class="[entry.Success ? 'bg-emerald-500/10 text-emerald-400' : 'bg-red-500/10 text-red-400', 'w-8 h-8 rounded-xl flex items-center justify-center shrink-0 border border-current/10']">
              <i :class="[entry.Success ? 'bi-check-lg' : 'bi-exclamation-triangle', 'text-sm']"></i>
            </div>
            <div class="min-w-0 flex-grow">
              <p class="text-[11px] font-black text-white/80 truncate italic group-hover/item:text-blue-400 transition-colors">{{ entry.OriginalUrl }}</p>
              <div class="flex items-center gap-2 mt-1">
                <span class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">{{ TIER_DISPLAY[entry.Tier.split('-')[0]]?.short || entry.Tier }}</span>
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

      <div class="bg-white/[0.02] border border-white/5 rounded-[32px] p-8 space-y-6 backdrop-blur-3xl group transition-shadow duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)]">
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

<style scoped>
@keyframes pulse-glow {
  0%, 100% {
    opacity: 0.3;
    transform: scale(1);
  }
  50% {
    opacity: 0.7;
    transform: scale(1.15);
  }
}
</style>
