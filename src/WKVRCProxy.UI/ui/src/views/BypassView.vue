<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useAppStore } from '../stores/appStore'
import type { BypassMemoryRow, BypassMemoryEntry } from '../stores/appStore'

const appStore = useAppStore()

onMounted(() => {
  appStore.refreshBypassMemory()
  appStore.refreshYtDlpUpdate()
})

const hostCount = computed(() => appStore.bypassMemory.length)
const totalEntries = computed(() =>
  appStore.bypassMemory.reduce((sum: number, row: BypassMemoryRow) => sum + row.entries.length, 0)
)

const rows = computed(() => {
  return [...appStore.bypassMemory].sort((a: BypassMemoryRow, b: BypassMemoryRow) => {
    const aBest = bestEntry(a.entries)
    const bBest = bestEntry(b.entries)
    const aTime = aBest ? new Date(aBest.lastSuccess).getTime() : 0
    const bTime = bBest ? new Date(bBest.lastSuccess).getTime() : 0
    return bTime - aTime
  })
})

function bestEntry(entries: BypassMemoryEntry[]): BypassMemoryEntry | null {
  if (entries.length === 0) return null
  let best: BypassMemoryEntry = entries[0]
  for (const e of entries) {
    if (e.netScore > best.netScore) best = e
    else if (e.netScore === best.netScore &&
             new Date(e.lastSuccess).getTime() > new Date(best.lastSuccess).getTime()) best = e
  }
  return best
}

function otherEntries(entries: BypassMemoryEntry[]): BypassMemoryEntry[] {
  const best = bestEntry(entries)
  return best ? entries.filter(e => e.strategy !== best.strategy) : []
}

function formatHost(key: string): string {
  const parts = key.split(':')
  return parts[0] ?? key
}

function streamKind(key: string): string {
  const parts = key.split(':')
  return parts[1] ?? ''
}

function relative(iso: string | null | undefined): string {
  if (!iso) return '—'
  const t = new Date(iso).getTime()
  if (!Number.isFinite(t) || t <= 0) return '—'
  const diff = Date.now() - t
  if (diff < 0) return 'just now'
  const s = Math.floor(diff / 1000)
  if (s < 60) return s + 's ago'
  const m = Math.floor(s / 60)
  if (m < 60) return m + 'm ago'
  const h = Math.floor(m / 60)
  if (h < 24) return h + 'h ago'
  const d = Math.floor(h / 24)
  return d + 'd ago'
}

function isDemoted(entry: BypassMemoryEntry): boolean {
  return entry.consecutiveFailures >= 3
}

function isStale(entry: BypassMemoryEntry): boolean {
  const t = new Date(entry.lastSuccess).getTime()
  if (!Number.isFinite(t) || t <= 0) return true
  return Date.now() - t > 30 * 24 * 60 * 60 * 1000
}

function forget(key: string) {
  appStore.forgetBypassKey(key)
}

function updateStatusColor(s: string): string {
  switch (s) {
    case 'Updated': return 'text-emerald-400'
    case 'UpToDate': return 'text-emerald-400'
    case 'UpdateAvailable':
    case 'Downloading':
    case 'Checking': return 'text-blue-400'
    case 'Failed': return 'text-red-400'
    case 'Disabled': return 'text-white/30'
    default: return 'text-white/50'
  }
}

function updateStatusLabel(s: string): string {
  switch (s) {
    case 'UpToDate': return 'Up to date'
    case 'UpdateAvailable': return 'Update available'
    default: return s
  }
}
</script>

<template>
  <div class="p-8 space-y-8 animate-in slide-in-from-bottom-4 duration-500">
    <!-- Header -->
    <div class="flex items-start justify-between">
      <div class="space-y-2">
        <h2 class="text-3xl font-black uppercase tracking-tighter italic">Bypass <span class="text-blue-500">Health</span></h2>
        <p class="text-white/45 font-black uppercase tracking-[0.4em] text-[9px] ml-1">What the resolver has learned</p>
      </div>
      <button
        @click="appStore.refreshBypassMemory()"
        class="px-4 py-2 rounded-xl text-[8px] font-black uppercase tracking-widest bg-white/[0.03] hover:bg-white/[0.06] border border-white/5 text-white/60 hover:text-white transition-all italic">
        <i class="bi bi-arrow-clockwise mr-1"></i> Refresh
      </button>
    </div>

    <!-- yt-dlp updater card -->
    <div class="bg-white/[0.02] border border-white/5 rounded-3xl p-6 backdrop-blur-3xl space-y-3">
      <div class="flex items-start justify-between gap-6">
        <div class="space-y-1.5">
          <p class="text-[8px] font-black uppercase tracking-[0.3em] text-white/40">yt-dlp Updater</p>
          <p class="font-mono text-[11px] text-white/80">
            local <span class="text-white/50">{{ appStore.ytDlpUpdate.localVersion || '—' }}</span>
            <span v-if="appStore.ytDlpUpdate.remoteVersion" class="text-white/30"> / latest </span>
            <span v-if="appStore.ytDlpUpdate.remoteVersion" class="text-white/70">{{ appStore.ytDlpUpdate.remoteVersion }}</span>
          </p>
          <p v-if="appStore.ytDlpUpdate.detail" class="text-[10px] text-white/40 italic">{{ appStore.ytDlpUpdate.detail }}</p>
        </div>
        <span :class="['text-[9px] font-black uppercase tracking-widest italic', updateStatusColor(appStore.ytDlpUpdate.status)]">
          {{ updateStatusLabel(appStore.ytDlpUpdate.status) }}
        </span>
      </div>
    </div>

    <!-- Stats row -->
    <div class="grid grid-cols-2 gap-3">
      <div class="bg-white/[0.03] border border-white/5 rounded-xl px-4 py-3">
        <div class="text-lg font-black italic tabular-nums text-white/80">{{ hostCount }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45">Hosts Tracked</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-xl px-4 py-3">
        <div class="text-lg font-black italic tabular-nums text-blue-400">{{ totalEntries }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45">Strategy Entries</div>
      </div>
    </div>

    <!-- Table -->
    <div class="bg-white/[0.02] border border-white/5 rounded-3xl overflow-hidden backdrop-blur-3xl shadow-2xl">
      <table class="w-full text-left text-[10px]">
        <thead class="bg-white/[0.01] text-white/50 font-black uppercase tracking-[0.2em]">
          <tr>
            <th class="px-6 py-4">Host</th>
            <th class="px-6 py-4">Best Strategy</th>
            <th class="px-6 py-4 text-right">W / L</th>
            <th class="px-6 py-4 text-right">Last Success</th>
            <th class="px-6 py-4 text-right">Action</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-white/5 font-bold">
          <template v-for="row in rows" :key="row.key">
            <tr class="hover:bg-white/[0.03] transition-all duration-300 group">
              <td class="px-6 py-4">
                <div class="flex flex-col gap-1">
                  <span class="text-white/80 tracking-tight">{{ formatHost(row.key) }}</span>
                  <span class="text-[8px] text-white/35 font-mono italic uppercase tracking-widest">{{ streamKind(row.key) }}</span>
                </div>
              </td>
              <td class="px-6 py-4">
                <template v-if="bestEntry(row.entries)">
                  <div class="flex items-center gap-2 flex-wrap">
                    <span class="px-3 py-1 bg-white/5 rounded-lg text-[8px] font-black uppercase tracking-widest border border-white/5 italic font-mono">
                      {{ bestEntry(row.entries)!.strategy }}
                    </span>
                    <span v-if="isDemoted(bestEntry(row.entries)!)"
                          class="px-2 py-0.5 rounded-md text-[7px] font-black uppercase tracking-widest bg-red-500/15 border border-red-500/25 text-red-400">
                      Demoted
                    </span>
                    <span v-else-if="isStale(bestEntry(row.entries)!)"
                          class="px-2 py-0.5 rounded-md text-[7px] font-black uppercase tracking-widest bg-amber-500/15 border border-amber-500/25 text-amber-400">
                      Stale
                    </span>
                  </div>
                </template>
                <span v-else class="text-white/25 font-mono text-[9px]">—</span>
              </td>
              <td class="px-6 py-4 text-right tabular-nums font-mono">
                <template v-if="bestEntry(row.entries)">
                  <span class="text-emerald-400">{{ bestEntry(row.entries)!.successCount }}</span>
                  <span class="text-white/25"> / </span>
                  <span class="text-red-400">{{ bestEntry(row.entries)!.failureCount }}</span>
                </template>
                <span v-else class="text-white/25">—</span>
              </td>
              <td class="px-6 py-4 text-right text-white/50 font-mono tabular-nums">
                {{ bestEntry(row.entries) ? relative(bestEntry(row.entries)!.lastSuccess) : '—' }}
              </td>
              <td class="px-6 py-4 text-right">
                <button
                  @click="forget(row.key)"
                  class="px-3 py-1.5 rounded-lg text-[8px] font-black uppercase tracking-widest bg-white/[0.03] hover:bg-red-500/20 hover:text-red-400 border border-white/5 text-white/50 transition-all italic">
                  <i class="bi bi-trash mr-1"></i> Forget
                </button>
              </td>
            </tr>
            <!-- Secondary entries (if more than one strategy for this host) -->
            <tr v-if="row.entries.length > 1" class="bg-black/20">
              <td colspan="5" class="px-6 py-3">
                <div class="flex flex-wrap gap-2 text-[9px]">
                  <span class="text-white/30 font-black uppercase tracking-[0.2em] mr-1">Also tried:</span>
                  <span v-for="entry in otherEntries(row.entries)" :key="entry.strategy"
                        :class="['font-mono px-2 py-0.5 rounded border',
                                 isDemoted(entry) ? 'text-red-400/70 border-red-500/20 bg-red-500/5' : 'text-white/50 border-white/5 bg-white/[0.02]']">
                    {{ entry.strategy }}
                    <span class="text-white/30 ml-1">{{ entry.successCount }}W / {{ entry.failureCount }}L</span>
                  </span>
                </div>
              </td>
            </tr>
          </template>
          <tr v-if="rows.length === 0">
            <td colspan="5" class="px-6 py-24 text-center">
              <div class="flex flex-col items-center gap-4 animate-pulse">
                <i class="bi bi-lightning-charge text-4xl text-white/10"></i>
                <div class="space-y-2">
                  <p class="text-white/25 font-black uppercase tracking-[0.5em] italic text-[10px]">No Memory Yet</p>
                  <p class="text-white/15 text-[9px] font-mono italic max-w-xs mx-auto">
                    The resolver will learn per-site bypass strategies as you play videos in VRChat.
                  </p>
                </div>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
