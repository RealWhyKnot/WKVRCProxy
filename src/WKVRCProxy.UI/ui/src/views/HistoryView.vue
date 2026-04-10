<script setup lang="ts">
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

const tierDisplay: Record<string, { short: string, long: string }> = {
  'tier1': { short: 'Local', long: 'Fastest, offline-capable local extraction.' },
  'tier2': { short: 'Cloud', long: 'Reliable cloud-based resolution via whyknot.dev.' },
  'tier3': { short: 'VRChat', long: 'Original VRChat yt-dlp fallback.' },
  'tier4': { short: 'Direct', long: 'No resolution, raw URL passthrough.' }
}

function formatTime(ts: string) {
  return new Date(ts).toLocaleTimeString()
}

function truncate(str: string, len: number) {
  if (str.length <= len) return str
  return str.substring(0, len) + '...'
}
</script>

<template>
  <div class="p-8 space-y-8 animate-in slide-in-from-bottom-4 duration-500">
    <div class="space-y-2">
      <h2 class="text-3xl font-black uppercase tracking-tighter italic">Resolution <span class="text-blue-500">History</span></h2>
      <p class="text-white/45 font-black uppercase tracking-[0.4em] text-[9px] ml-1">All resolved requests</p>
    </div>

    <div class="bg-white/[0.02] border border-white/5 rounded-3xl overflow-hidden backdrop-blur-3xl shadow-2xl">
      <table class="w-full text-left text-[10px]">
        <thead class="bg-white/[0.01] text-white/50 font-black uppercase tracking-[0.2em]">
          <tr>
            <th class="px-6 py-4">Time</th>
            <th class="px-6 py-4">Source URL</th>
            <th class="px-6 py-4">Tier</th>
            <th class="px-6 py-4">Type</th>
            <th class="px-6 py-4 text-right">Result</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-white/5 font-bold">
          <tr v-for="(entry, i) in appStore.config.history" :key="i" class="hover:bg-white/[0.03] transition-all duration-300 group">
            <td class="px-6 py-4 text-white/50 font-mono tabular-nums">{{ formatTime(entry.Timestamp) }}</td>
            <td class="px-6 py-4">
              <div class="flex flex-col gap-1">
                <span class="text-white/80 group-hover:text-blue-400 transition-colors tracking-tight truncate max-w-md">{{ entry.OriginalUrl }}</span>
                <span class="text-[8px] text-white/35 font-mono italic group-hover:text-white/50 transition-colors">{{ truncate(entry.ResolvedUrl, 80) }}</span>
              </div>
            </td>
            <td class="px-6 py-4">
              <div class="flex items-center gap-2">
                <span :title="tierDisplay[entry.Tier.split('-')[0]]?.long" class="px-3 py-1 bg-white/5 rounded-lg text-[8px] font-black uppercase tracking-widest border border-white/5 group-hover:border-blue-500/20 transition-all italic">
                  {{ tierDisplay[entry.Tier.split('-')[0]]?.short || entry.Tier }}
                </span>
                <i :title="entry.Player" :class="entry.Player === 'AVPro' ? 'bi-camera-video-fill text-purple-400/70' : 'bi-play-circle-fill text-blue-400/70'" class="bi text-xs"></i>
              </div>
            </td>
            <td class="px-6 py-4">
              <span v-if="entry.IsLive" class="px-3 py-1 bg-green-500/15 rounded-lg text-[8px] font-black uppercase border border-green-500/25 text-green-400 flex items-center gap-1.5 w-fit">
                <span class="w-1 h-1 bg-green-400 rounded-full animate-pulse inline-block"></span>LIVE
              </span>
              <span v-else class="px-3 py-1 bg-white/5 rounded-lg text-[8px] font-black uppercase border border-white/5 text-white/45 italic">VOD</span>
            </td>
            <td class="px-6 py-4 text-right">
              <span :class="entry.Success ? 'text-emerald-400' : 'text-red-400'" class="font-black italic text-[9px]">
                {{ entry.Success ? 'OK' : 'FAILED' }}
              </span>
            </td>
          </tr>
          <tr v-if="appStore.config.history.length === 0">
            <td colspan="5" class="px-6 py-20 text-center text-white/25 font-black uppercase tracking-[0.5em] italic">No Records</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
