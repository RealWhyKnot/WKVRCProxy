import { defineStore } from 'pinia'
import { ref, computed } from 'vue'

export const TIER_DISPLAY: Record<string, { short: string; long: string; color: string }> = {
  tier1: { short: 'Local',        long: 'Fastest, offline-capable local extraction.',            color: 'bg-blue-500'   },
  tier2: { short: 'Cloud',        long: 'Reliable cloud-based resolution via WhyKnot.dev.',      color: 'bg-purple-500' },
  tier3: { short: 'VRChat Tools', long: 'Original VRChat yt-dlp behavior.',                      color: 'bg-amber-500'  },
  tier4: { short: 'Passthrough',  long: 'No resolution, returns raw original URL.',              color: 'bg-white/20'   },
}

export interface RelayEvent {
  id: string;
  timestamp: string;
  targetUrl: string;
  method: string;
  statusCode: number;
  bytesTransferred: number;
}

export interface LogEntry {
  Timestamp: string;
  Level: number;
  Message: string;
  Source: string;
}

export interface HistoryEntry {
  Timestamp: string;
  OriginalUrl: string;
  ResolvedUrl: string;
  Tier: string;
  Player: string;
  Success: boolean;
  IsLive: boolean;
  StreamType: string; // "live" | "vod" | "unknown"
}

export interface AppConfig {
  debugMode: boolean;
  preferredResolution: string;
  forceIPv4: boolean;
  autoPatchOnStart: boolean;
  preferredTier: string;
  history: HistoryEntry[];
  userAgent: string;
  customVrcPath?: string;
  bypassHostsSetupDeclined?: boolean;
  enableRelayBypass: boolean;
  disabledTiers: string[];
}

export interface AppStatus {
  message: string;
  stats: {
    activeCount: number;
    tierStats: Record<string, number>;
    node: string;
    player: string;
  }
}

export const useAppStore = defineStore('app', () => {
  const activeTab = ref('dashboard')
  const logs = ref<LogEntry[]>([])
  const logLevelFilter = ref<number | null>(null) // null = show all levels
  const logSourceFilter = ref<string>('')          // '' = show all sources

  const filteredLogs = computed(() => {
    return logs.value.filter(entry => {
      if (logLevelFilter.value !== null && entry.Level !== logLevelFilter.value) return false
      if (logSourceFilter.value && !entry.Source.toLowerCase().includes(logSourceFilter.value.toLowerCase())) return false
      return true
    })
  })
  
  const status = ref<AppStatus>({
    message: 'Ready',
    stats: {
      activeCount: 0,
      tierStats: { tier1: 0, tier2: 0, tier3: 0, tier4: 0 },
      node: 'None',
      player: 'None'
    }
  })

  const config = ref<AppConfig>({
    debugMode: true,
    preferredResolution: '1080p',
    forceIPv4: false,
    autoPatchOnStart: true,
    preferredTier: 'tier1',
    history: [],
    userAgent: '',
    bypassHostsSetupDeclined: false,
    enableRelayBypass: true,
    disabledTiers: []
  })
  
  const showHostsPrompt = ref(false)
  const relayEvents = ref<RelayEvent[]>([])
  
  const isBridgeReady = ref(false)
  const version = ref('2026.4.10.5-3474')

  function handleMessage(message: string) {
    try {
      const parsed = JSON.parse(message)
      if (parsed.type === 'LOG') {
        const entry = parsed.data as LogEntry
        if (!logs.value.some(l => l.Message === entry.Message && l.Timestamp === entry.Timestamp)) {
          logs.value.push(entry)
          if (logs.value.length > 1000) logs.value.shift()
        }
      } else if (parsed.type === 'CONFIG') {
        config.value = parsed.data
      } else if (parsed.type === 'STATUS') {
        status.value = parsed.data
      } else if (parsed.type === 'PROMPT_HOSTS_SETUP') {
        showHostsPrompt.value = true
      } else if (parsed.type === 'RELAY_EVENT') {
        const e = parsed.data as RelayEvent;
        const idx = relayEvents.value.findIndex(x => x.id === e.id);
        if (idx >= 0) {
          relayEvents.value[idx] = e;
        } else {
          relayEvents.value.unshift(e);
          if (relayEvents.value.length > 100) relayEvents.value.pop();
        }
      }
    } catch (e) { }
  }

  function sendMessage(type: string, data?: any) {
    // @ts-ignore
    if (window.photino) {
      // @ts-ignore
      window.photino.sendMessage(JSON.stringify({ type, data }))
    }
  }

  function initBridge() {
    // @ts-ignore
    if (window.photino && window.photino.receiveMessage) {
      // @ts-ignore
      window.photino.receiveMessage(handleMessage)
      sendMessage('SYNC_LOGS')
      sendMessage('GET_CONFIG')
      isBridgeReady.value = true
      return true
    }
    return false
  }

  function saveConfig() {
    sendMessage('SAVE_CONFIG', config.value)
  }

  function pickVrcPath() {
    sendMessage('PICK_VRC_PATH')
  }

  function wipeTools() {
    sendMessage('WIPE_TOOLS')
  }

  function terminate() {
    sendMessage('EXIT')
  }

  const successRate = computed(() => {
    const history = config.value.history
    if (history.length === 0) return 0
    const successes = history.filter(h => h.Success).length
    return Math.round((successes / history.length) * 100)
  })

  const liveStreamCount = computed(() => {
    return config.value.history.filter(h => h.IsLive).length
  })

  const totalBytesTransferred = computed(() => {
    return relayEvents.value.reduce((sum, e) => sum + e.bytesTransferred, 0)
  })

  function clearHistory() {
    config.value.history = []
    saveConfig()
  }

  function clearLogs() {
    logs.value = []
  }

  return {
    activeTab,
    logs,
    filteredLogs,
    logLevelFilter,
    logSourceFilter,
    config,
    status,
    isBridgeReady,
    version,
    showHostsPrompt,
    initBridge,
    sendMessage,
    saveConfig,
    pickVrcPath,
    wipeTools,
    terminate,
    relayEvents,
    successRate,
    liveStreamCount,
    totalBytesTransferred,
    clearHistory,
    clearLogs
  }
})









































