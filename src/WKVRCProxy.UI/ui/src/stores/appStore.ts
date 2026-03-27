import { defineStore } from 'pinia'
import { ref } from 'vue'

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
    enableRelayBypass: true
  })
  
  const showHostsPrompt = ref(false)
  const relayEvents = ref<RelayEvent[]>([])
  
  const isBridgeReady = ref(false)
  const version = ref('2026.3.26.4-0E6E')

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

  return {
    activeTab,
    logs,
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
    relayEvents
  }
})






















