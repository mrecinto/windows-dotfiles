import * as zebar from 'https://esm.sh/zebar@3.3.1?bundle';

const get = id => document.getElementById(id);
const numbered = Array.from({ length: 10 }, (_, index) => String(index + 1));
const special = ['W', 'A', 'S', 'D', 'I', 'O'];
const specialToggle = get('special');
const specialMenu = get('special-workspaces');
const providers = zebar.createProviderGroup({
  glazewm: { type: 'glazewm' },
  battery: { type: 'battery' },
  network: { type: 'network' },
  audio: { type: 'audio' },
});

function showSpecial(show) {
  specialMenu.hidden = !show;
  specialToggle.setAttribute('aria-expanded', String(show));
}

specialToggle.addEventListener('click', () => showSpecial(specialMenu.hidden));
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && !specialMenu.hidden) {
    showSpecial(false);
    specialToggle.focus();
  }
});
document.addEventListener('click', event => {
  if (!specialToggle.contains(event.target) && !specialMenu.contains(event.target)) showSpecial(false);
});

function makeButton(name, parent) {
  const button = document.createElement('button');
  button.textContent = name;
  button.setAttribute('aria-label', `Workspace ${name}`);
  button.addEventListener('click', () => {
    providers.outputMap.glazewm?.runCommand(`focus --workspace ${name}`);
    showSpecial(false);
  });
  parent.append(button);
  return button;
}

const buttons = new Map([
  ...numbered.map(name => [name, makeButton(name, get('workspaces'))]),
  ...special.map(name => [name, makeButton(name, specialMenu)]),
]);

function workspaceApps(container) {
  if (container.type === 'window') return [container.processName];
  return (container.children ?? []).flatMap(workspaceApps);
}

function status(id, label, text, warn = false) {
  const element = get(id);
  element.hidden = !text;
  element.textContent = text;
  element.title = label;
  element.setAttribute('aria-label', label);
  element.classList.toggle(id === 'network' ? 'offline' : 'low', warn);
}

function render() {
  const { glazewm, battery, network, audio } = providers.outputMap;
  if (glazewm) {
    const workspaces = new Map(glazewm.allWorkspaces.map(workspace => [workspace.name, workspace]));
    for (const [name, button] of buttons) {
      const workspace = workspaces.get(name);
      const apps = workspace ? [...new Set(workspaceApps(workspace).filter(Boolean))] : [];
      button.classList.toggle('focused', Boolean(workspace?.hasFocus));
      button.classList.toggle('displayed', Boolean(workspace?.isDisplayed));
      button.classList.toggle('occupied', apps.length > 0);
      button.setAttribute('aria-pressed', String(Boolean(workspace?.hasFocus)));
      button.title = apps.join(', ') || 'Empty';
    }

    const focusedName = glazewm.focusedWorkspace?.name;
    const focusedSpecial = special.includes(focusedName);
    specialToggle.textContent = focusedSpecial ? focusedName : '✦';
    specialToggle.classList.toggle('focused', focusedSpecial);
    const window = glazewm.focusedContainer;
    const title = window?.type === 'window' ? [window.title, window.processName].filter(Boolean).join(' — ') : '';
    get('focused-window').textContent = title;
    get('focused-window').title = title;
  }
  if (battery) {
    const charge = Math.round(battery.chargePercent);
    status('battery', `Battery ${charge}%${battery.isCharging ? ', charging' : ''}`,
      `${battery.isCharging ? '⚡' : '▰'} ${charge}%`, charge <= 15 && !battery.isCharging);
  }
  if (network) {
    const connected = Boolean(network.defaultInterface);
    status('network', network.defaultGateway?.ssid ?? (connected ? 'Connected' : 'Offline'),
      connected ? '◉' : '○', !connected);
  }
  if (audio?.defaultPlaybackDevice) {
    const { volume, isMuted } = audio.defaultPlaybackDevice;
    if (Number.isFinite(volume)) {
      const percent = Math.round(volume);
      status('volume', isMuted ? 'Muted' : `Volume ${percent}%`,
        isMuted ? '♪ mute' : `♪ ${percent}%`);
    }
  }
}

const dateFormat = new Intl.DateTimeFormat('en-US', { weekday: 'short', month: 'short', day: '2-digit' });
const timeFormat = new Intl.DateTimeFormat('en-US', { hour: '2-digit', minute: '2-digit', hour12: true });
function updateClock() {
  const now = new Date();
  const clock = get('clock');
  clock.dateTime = now.toISOString();
  clock.textContent = `${dateFormat.format(now).replace(',', '')}  ${timeFormat.format(now)}`;
}

const paletteKeys = ['background', 'on_background', 'primary', 'on_primary', 'surface_variant', 'surface_tint', 'error'];
const script = 'C:\\Users\\Matthew\\Personal\\dotfiles\\.config\\zebar\\wallpaper.ps1';
let latestPalette = '';

async function updatePalette() {
  try {
    const { stdout, stderr } = await zebar.shellExec('powershell.exe', [
      '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File', script,
    ]);
    const text = stdout.trim();
    if (!text) throw new Error(stderr.trim() || 'Wallpaper helper returned no colors');
    if (text === latestPalette) return;
    const colors = JSON.parse(text);
    if (!paletteKeys.every(key => /^#[0-9a-f]{6}$/i.test(colors[key]))) {
      throw new Error('Wallpaper palette was incomplete');
    }
    for (const key of paletteKeys) {
      document.documentElement.style.setProperty('--' + key.replaceAll('_', '-'), colors[key]);
    }
    latestPalette = text;
  } catch (error) {
    console.warn('Wallpaper palette:', error);
  } finally {
    window.setTimeout(updatePalette, 5000);
  }
}

async function followVirtualDesktop() {
  const watcher = await zebar.shellSpawn(
    'C:\\Users\\Matthew\\Personal\\dotfiles\\.config\\zebar\\watch-virtual-desktop.exe',
  );
  let previousWorkspace = providers.outputMap.glazewm?.focusedWorkspace?.id;
  let pendingFocus;
  function hasOpenWindow(container) {
    if (container.type === 'window') return container.state?.type !== 'minimized';
    return (container.children ?? []).some(hasOpenWindow);
  }

  providers.onOutput(() => {
    const state = providers.outputMap.glazewm;
    const workspace = state?.focusedWorkspace;
    if (!workspace || workspace.id === previousWorkspace) return;

    const hadWorkspace = Boolean(previousWorkspace);
    previousWorkspace = workspace.id;
    window.clearTimeout(pendingFocus);
    if (!hadWorkspace || state.currentMonitor?.id !== state.focusedMonitor?.id) return;

    // Wait for GlazeWM to finish showing the destination workspace's windows.
    pendingFocus = window.setTimeout(() => {
      const latest = providers.outputMap.glazewm;
      const monitor = latest?.focusedMonitor;
      const target = latest?.focusedWorkspace;
      if (!monitor || target?.id !== workspace.id || hasOpenWindow(target)) return;
      const x = Math.round(monitor.x + monitor.width / 2);
      const y = Math.round(monitor.y + monitor.height / 2);
      Promise.resolve(watcher.write(`focus ${x} ${y}\n`))
        .catch(error => console.warn('Fullscreen focus:', error));
    }, 120);
  });

  window.addEventListener('pagehide', () => {
    window.clearTimeout(pendingFocus);
    void watcher.kill().catch(() => {});
  }, { once: true });
}

providers.onOutput(render);
render();
updateClock();
window.setInterval(updateClock, 1000);
void updatePalette();
void followVirtualDesktop().catch(error => console.warn('Virtual desktop watcher:', error));
