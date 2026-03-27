<script setup lang="ts">
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

const tierDisplay: Record<string, { short: string, long: string }> = {
  'tier1': { short: 'Tier 1', long: 'Local extraction logic.' },
  'tier2': { short: 'Tier 2', long: 'Cloud-based resolution.' },
  'tier3': { short: 'Tier 3', long: 'Original VRChat tools.' },
  'tier4': { short: 'Tier 4', long: 'Direct URL passthrough.' }
}

function truncate(str: string, len: number) {
  if (str.length <= len) return str
  return str.substring(0, len) + '...'
}

function clearCustomPath() {
  appStore.config.customVrcPath = undefined
  appStore.saveConfig()
}
</script>

<template>
  <div class="p-8 max-w-4xl space-y-10 animate-in zoom-in-95 duration-700">
    <div class="space-y-2">
      <h2 class="text-3xl font-black uppercase tracking-tighter italic">Settings</h2>
      <p class="text-white/20 font-black uppercase tracking-[0.4em] text-[8px] ml-1">Configuration</p>
    </div>

    <div class="space-y-6">
      <!-- VRChat Path Selection -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-6 hover:border-blue-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl group">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 bg-blue-500/10 rounded-2xl flex items-center justify-center text-blue-500 group-hover:scale-110 transition-transform">
            <i class="bi bi-folder-fill text-xl"></i>
          </div>
          <div>
            <h4 class="text-lg font-black uppercase tracking-tighter italic">VRChat Tools Path</h4>
            <p class="text-[8px] text-white/20 font-black uppercase tracking-widest mt-0.5">Where the game stores video tools</p>
          </div>
        </div>

        <div class="flex gap-3">
          <div class="flex-grow bg-white/[0.02] border border-white/5 rounded-2xl px-6 py-3 flex items-center overflow-hidden group-hover:bg-white/[0.04] transition-colors">
            <span class="text-[9px] font-mono text-white/40 truncate italic">
              {{ appStore.config.customVrcPath || 'Detecting automatically...' }}
            </span>
          </div>
          <button @click="appStore.pickVrcPath()" class="px-6 py-3 bg-blue-600 hover:bg-blue-500 text-white rounded-2xl font-black text-[9px] uppercase tracking-[0.2em] transition-all italic active:scale-95 shadow-xl shadow-blue-600/20">
            Change Path
          </button>
          <button v-if="appStore.config.customVrcPath" @click="clearCustomPath()" class="px-4 bg-white/5 hover:bg-red-500/20 text-white/40 hover:text-red-400 rounded-2xl transition-all border border-white/5 active:scale-90">
            <i class="bi bi-x-lg"></i>
          </button>
        </div>
      </section>

      <div class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] flex items-center justify-between group hover:border-blue-500/20 transition-all duration-500 backdrop-blur-3xl shadow-xl">
        <div class="max-w-md">
          <h4 class="text-lg font-black uppercase tracking-tighter mb-0.5 italic">Preferred Resolution Tier</h4>
          <p class="text-[8px] text-white/20 font-black uppercase tracking-widest">Which extraction method to try first</p>
        </div>
        <select v-model="appStore.config.preferredTier" @change="appStore.saveConfig()" class="bg-[#010103] border border-white/10 rounded-xl px-6 py-3 text-[10px] font-black uppercase tracking-widest focus:outline-none focus:border-blue-500 transition-all cursor-pointer text-white/80 hover:text-white italic appearance-none shadow-2xl">
          <option v-for="(data, id) in tierDisplay" :key="id" :value="id">{{ data.short }} - {{ truncate(data.long, 35) }}</option>
        </select>
      </div>

      <div class="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div @click="appStore.config.debugMode = !appStore.config.debugMode; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Detailed Logging</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.debugMode ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/5 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700 cubic-bezier(0.2, 0.8, 0.2, 1)', appStore.config.debugMode ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[8px] text-white/20 font-black uppercase tracking-widest leading-relaxed">Show more technical info in the logs.</p>
        </div>

        <div @click="appStore.config.forceIPv4 = !appStore.config.forceIPv4; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Force IPv4</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.forceIPv4 ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/5 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700 cubic-bezier(0.2, 0.8, 0.2, 1)', appStore.config.forceIPv4 ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[8px] text-white/20 font-black uppercase tracking-widest leading-relaxed">Only use IPv4 for video resolution.</p>
        </div>
      </div>

      <div class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] flex items-center justify-between group hover:border-blue-500/20 transition-all duration-500 backdrop-blur-3xl">
        <div class="max-w-md">
          <h4 class="text-lg font-black uppercase tracking-tighter mb-0.5 italic">Max Video Quality</h4>
          <p class="text-[8px] text-white/20 font-black uppercase tracking-widest">Resolution limit for video players</p>
        </div>
        <div class="flex gap-2">
          <button v-for="res in ['480p', '720p', '1080p', '1440p']" :key="res"
                  @click="appStore.config.preferredResolution = res; appStore.saveConfig()"
                  :class="['px-4 py-2 rounded-xl text-[9px] font-black uppercase tracking-widest transition-all italic', appStore.config.preferredResolution === res ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.3)] text-white' : 'bg-white/5 border border-white/5 text-white/40 hover:border-white/20 hover:text-white']">
            {{ res }}
          </button>
        </div>
      </div>

      <div class="pt-8 border-t border-white/5 flex items-center justify-between group">
        <div>
          <h4 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/40 mb-1 italic group-hover:text-blue-400 transition-colors">Network Authorization</h4>
          <p class="text-[8px] text-white/20 font-bold uppercase tracking-widest">Re-prompt for hosts file bypass</p>
        </div>
        <button @click="appStore.sendMessage('REQUEST_HOSTS_SETUP')" class="px-8 py-4 rounded-2xl bg-white/5 border border-white/5 text-white/40 hover:bg-blue-500/10 hover:text-blue-400 hover:border-blue-500/20 transition-all text-[9px] font-black uppercase tracking-[0.2em] italic active:scale-95 shadow-xl shadow-transparent hover:shadow-blue-500/10">
          Request Prompt
        </button>
      </div>

      <div class="pt-4 border-t border-white/5 flex items-center justify-between">
        <div>
          <h4 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/10 mb-1 italic">Maintenance</h4>
          <p class="text-[8px] text-white/5 font-bold uppercase tracking-widest">Reinstall local tools</p>
        </div>
        <button @click="appStore.wipeTools()" class="px-8 py-4 rounded-2xl bg-white/5 border border-white/5 text-white/20 hover:bg-red-500/10 hover:text-red-400 hover:border-red-500/20 transition-all text-[9px] font-black uppercase tracking-[0.2em] italic active:scale-95">
          Reset Tools
        </button>
      </div>
    </div>
  </div>
</template>