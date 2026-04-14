<script setup lang="ts">
import { ref, computed } from 'vue'
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

const mode = ref<'cloud' | 'p2p'>('cloud')

const modes = [
  { id: 'cloud', label: 'Cloud Link', icon: 'bi-link-45deg' },
  { id: 'p2p',   label: 'P2P Stream', icon: 'bi-broadcast' }
]

// Most recent successful resolution from history
const lastEntry = computed(() =>
  appStore.config.history.find(h => h.Success && h.ResolvedUrl && h.ResolvedUrl !== 'FAILED')
)

// Cloud Link (Mode B)
const copied = ref(false)
async function copyUrl() {
  const url = lastEntry.value?.ResolvedUrl
  if (!url) return
  await navigator.clipboard.writeText(url)
  copied.value = true
  setTimeout(() => { copied.value = false }, 2000)
}

// P2P Stream (Mode A)
const p2pCopied = ref(false)

function startP2PStream() {
  const url = lastEntry.value?.ResolvedUrl
  if (!url) return
  appStore.startP2PShare(url)
}

function stopP2PStream() {
  appStore.stopP2PShare()
}

async function copyP2PUrl() {
  if (!appStore.p2pSharePublicUrl) return
  await navigator.clipboard.writeText(appStore.p2pSharePublicUrl)
  p2pCopied.value = true
  setTimeout(() => { p2pCopied.value = false }, 2000)
}
</script>

<template>
  <div class="h-full flex flex-col p-8 animate-in fade-in duration-700 overflow-y-auto no-scrollbar">

    <!-- Header -->
    <div class="mb-6">
      <h2 class="text-2xl font-black uppercase tracking-tighter italic text-white/90">Share</h2>
      <p class="text-white/35 text-[9px] font-bold uppercase tracking-[0.2em] mt-1">Broadcast your stream to a friend via WhyKnot.dev</p>
    </div>

    <!-- Current video card -->
    <div v-if="lastEntry" class="mb-5 p-4 bg-white/[0.03] border border-white/5 rounded-2xl relative overflow-hidden">
      <div class="absolute -top-8 -right-8 w-28 h-28 bg-blue-500/5 blur-[60px] rounded-full"></div>
      <p class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30 mb-2">Current Video</p>
      <p class="text-white/75 text-[10px] font-mono truncate">{{ lastEntry.OriginalUrl }}</p>
      <p class="text-white/30 text-[8px] font-mono truncate mt-0.5">{{ lastEntry.ResolvedUrl }}</p>
      <div class="flex items-center gap-1.5 mt-2">
        <span class="text-[7px] font-black uppercase tracking-widest px-2 py-0.5 rounded-full bg-blue-500/20 text-blue-400">{{ lastEntry.Tier.toUpperCase() }}</span>
        <span class="text-[7px] font-black uppercase tracking-widest px-2 py-0.5 rounded-full"
              :class="lastEntry.IsLive ? 'bg-red-500/20 text-red-400' : 'bg-white/10 text-white/35'">
          {{ lastEntry.IsLive ? 'LIVE' : 'VOD' }}
        </span>
      </div>
    </div>
    <div v-else class="mb-5 p-5 bg-white/[0.02] border border-white/5 rounded-2xl text-center">
      <i class="bi bi-collection-play text-white/20 text-2xl mb-2 block"></i>
      <p class="text-white/30 text-[9px] font-bold uppercase tracking-widest">No recent resolution — play a video in VRChat first</p>
    </div>

    <!-- Mode selector -->
    <div class="flex gap-2 mb-5">
      <button v-for="m in modes" :key="m.id" @click="mode = (m.id as 'cloud' | 'p2p')"
              class="flex-1 py-3 rounded-2xl text-[8px] font-black uppercase tracking-widest transition-all duration-300 italic"
              :class="mode === m.id
                ? 'bg-blue-600 text-white shadow-lg shadow-blue-600/20'
                : 'bg-white/5 text-white/45 hover:bg-white/8 hover:text-white/65'">
        <i :class="'bi ' + m.icon + ' mr-1.5'"></i>{{ m.label }}
      </button>
    </div>

    <!-- ── Mode B: Cloud Link ── -->
    <template v-if="mode === 'cloud'">
      <div class="p-4 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3">
        <p class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30">Direct Stream URL</p>
        <p v-if="lastEntry" class="text-[8px] font-mono text-white/55 truncate leading-relaxed break-all whitespace-pre-wrap">{{ lastEntry.ResolvedUrl }}</p>
        <p v-else class="text-[8px] text-white/25 italic">No URL available</p>
        <button @click="copyUrl" :disabled="!lastEntry"
                class="w-full py-3.5 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic active:scale-95 disabled:opacity-30 disabled:cursor-not-allowed shadow-lg"
                :class="copied ? 'bg-green-600/80 text-white shadow-green-600/20' : 'bg-blue-600 hover:bg-blue-500 text-white shadow-blue-600/20'">
          <i :class="'bi mr-1.5 ' + (copied ? 'bi-check-lg' : 'bi-clipboard')"></i>{{ copied ? 'Copied!' : 'Copy URL' }}
        </button>
      </div>
      <p class="mt-3 text-white/20 text-[7px] text-center leading-relaxed">
        CDN URLs are signed and expire. Paste immediately into VRChat, a media player, or another player.
      </p>
    </template>

    <!-- ── Mode A: P2P Stream ── -->
    <template v-if="mode === 'p2p'">

      <!-- Idle -->
      <div v-if="appStore.p2pShareStatus === 'idle'" class="p-4 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3">
        <p class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30">Stream via WhyKnot.dev</p>
        <p class="text-white/45 text-[8px] leading-relaxed">
          The program connects to WhyKnot.dev and relays your video stream so a friend can watch from their browser or VRChat world — no account required.
        </p>
        <p class="text-amber-400/60 text-[8px] leading-relaxed">
          <i class="bi bi-exclamation-triangle mr-1"></i>Works best with direct video URLs (MP4/WebM). Live HLS streams may not relay correctly.
        </p>
        <button @click="startP2PStream" :disabled="!lastEntry"
                class="w-full py-3.5 bg-blue-600 hover:bg-blue-500 text-white rounded-xl font-black text-[8px] uppercase tracking-widest transition-all italic active:scale-95 disabled:opacity-30 disabled:cursor-not-allowed shadow-lg shadow-blue-600/20">
          <i class="bi bi-broadcast mr-1.5"></i>Start Streaming
        </button>
      </div>

      <!-- Connecting -->
      <div v-else-if="appStore.p2pShareStatus === 'connecting'"
           class="p-5 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3 text-center">
        <div class="flex justify-center gap-1.5 py-3">
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce"></div>
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce [animation-delay:0.15s]"></div>
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce [animation-delay:0.3s]"></div>
        </div>
        <p class="text-white/45 text-[8px] font-bold uppercase tracking-widest">Connecting to WhyKnot.dev...</p>
      </div>

      <!-- Active -->
      <div v-else-if="appStore.p2pShareStatus === 'active'"
           class="p-4 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3">
        <div class="flex items-center gap-2">
          <div class="w-2 h-2 bg-green-500 rounded-full animate-pulse shadow-[0_0_6px_rgba(34,197,94,0.8)]"></div>
          <p class="text-green-400 text-[8px] font-black uppercase tracking-widest">Streaming Active</p>
        </div>
        <p class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30">Share this link with your friend:</p>
        <p class="text-white/80 text-[8px] font-mono break-all leading-relaxed">{{ appStore.p2pSharePublicUrl }}</p>
        <div class="flex gap-2">
          <button @click="copyP2PUrl"
                  class="flex-1 py-3 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic active:scale-95 shadow-lg"
                  :class="p2pCopied ? 'bg-green-600/80 text-white shadow-green-600/20' : 'bg-blue-600 hover:bg-blue-500 text-white shadow-blue-600/20'">
            <i :class="'bi mr-1.5 ' + (p2pCopied ? 'bi-check-lg' : 'bi-clipboard')"></i>{{ p2pCopied ? 'Copied!' : 'Copy Link' }}
          </button>
          <button @click="stopP2PStream"
                  class="px-4 py-3 bg-white/5 hover:bg-red-500/20 hover:text-red-400 text-white/45 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all">
            <i class="bi bi-stop-fill"></i>
          </button>
        </div>
      </div>

      <!-- Error -->
      <div v-else-if="appStore.p2pShareStatus === 'error'"
           class="p-4 bg-red-500/10 border border-red-500/20 rounded-2xl space-y-3">
        <p class="text-red-400 text-[8px] font-black uppercase tracking-widest">
          <i class="bi bi-exclamation-triangle mr-1.5"></i>Stream Failed
        </p>
        <p class="text-red-400/65 text-[8px] leading-relaxed">{{ appStore.p2pShareError }}</p>
        <button @click="startP2PStream" :disabled="!lastEntry"
                class="w-full py-3 bg-white/5 hover:bg-white/8 text-white/65 rounded-xl font-black text-[8px] uppercase tracking-widest transition-all italic active:scale-95 disabled:opacity-30">
          <i class="bi bi-arrow-clockwise mr-1.5"></i>Retry
        </button>
      </div>

    </template>

  </div>
</template>
