import type { AgentCapability, RunnerAgent, RunnersView } from '@/api/types';

/**
 * Settings → Runners fixtures (§33.3).
 *
 * Four agents, chosen so the capability set does real work rather than decorating a list:
 *
 * - a Linux Docker agent that can run most things,
 * - a Mac mini in `native` mode, because §33.2 is explicit that macOS with Xcode cannot be
 *   containerised,
 * - an embedded box with a physical STM32 on a USB port — the case §27.3 uses to argue that some
 *   work can only ever run on the operator's own hardware,
 * - an offline agent whose lease has expired, so the list has to say what happens to its jobs.
 *
 * Between them, the waiting sessions below route to some and not others, which is the question an
 * engineer actually arrives at this page with.
 */

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

const iso = (offsetMs: number, now: number): string => new Date(now + offsetMs).toISOString();

function capability(
  id: string,
  family: string,
  label: string,
  probedBy: string,
  now: number,
  version?: string,
): AgentCapability {
  return {
    id,
    family,
    label,
    probedBy,
    probedAt: iso(-3 * HOUR, now),
    ...(version === undefined ? {} : { version }),
  };
}

export function makeRunners(now: number): RunnersView {
  const agents: RunnerAgent[] = [
    {
      id: 'agent-linux-01',
      name: 'build-01',
      mode: 'docker',
      version: '0.4.1',
      protocolCompatible: true,
      status: 'online',
      lastHeartbeatAt: iso(-9_000, now),
      registeredAt: iso(-41 * DAY, now),
      os: 'Debian 12',
      arch: 'x86_64',
      concurrency: { limit: 4, inFlight: 2 },
      capabilities: [
        capability('linux', 'os', 'Linux', 'uname -s', now),
        capability('docker', 'container', 'Docker', 'docker version --format {{.Server.Version}}', now, '27.3.1'),
        capability('dotnet:10.0.100', 'dotnet', '.NET SDK', 'dotnet --list-sdks', now, '10.0.100'),
        capability('node:22.11.0', 'node', 'Node', 'node --version', now, '22.11.0'),
        capability('python:3.12.7', 'python', 'Python', 'python3 --version', now, '3.12.7'),
      ],
    },
    {
      id: 'agent-mac-01',
      name: 'mac-mini-01',
      mode: 'native',
      version: '0.4.1',
      protocolCompatible: true,
      status: 'online',
      lastHeartbeatAt: iso(-4_000, now),
      registeredAt: iso(-120 * DAY, now),
      os: 'macOS 15.2',
      arch: 'arm64',
      concurrency: { limit: 2, inFlight: 1 },
      capabilities: [
        capability('macos', 'os', 'macOS', 'sw_vers -productVersion', now, '15.2'),
        capability('xcode:16.2', 'xcode', 'Xcode', 'xcodebuild -version', now, '16.2'),
        capability('signing', 'signing', 'Signing identity', 'security find-identity -v -p codesigning', now),
        capability('node:22.11.0', 'node', 'Node', 'node --version', now, '22.11.0'),
      ],
    },
    {
      id: 'agent-bench-01',
      name: 'bench-pi',
      mode: 'native',
      version: '0.3.9',
      protocolCompatible: false,
      protocolNote:
        'Speaks protocol 2; this Charter speaks 3. It will not claim work until it is upgraded.',
      status: 'online',
      lastHeartbeatAt: iso(-11_000, now),
      registeredAt: iso(-7 * DAY, now),
      os: 'Raspberry Pi OS',
      arch: 'aarch64',
      concurrency: { limit: 1, inFlight: 0 },
      capabilities: [
        capability('linux', 'os', 'Linux', 'uname -s', now),
        capability('toolchain:arm-none-eabi', 'toolchain', 'ARM toolchain', 'arm-none-eabi-gcc --version', now, '13.2'),
        capability('usb_device:stm32f4', 'usb_device', 'USB device', 'probe-rs list', now),
      ],
    },
    {
      id: 'agent-win-01',
      name: 'win-builder',
      mode: 'docker',
      version: '0.4.0',
      protocolCompatible: true,
      status: 'offline',
      lastHeartbeatAt: iso(-3 * HOUR - 12 * MINUTE, now),
      registeredAt: iso(-64 * DAY, now),
      os: 'Windows Server 2022',
      arch: 'x86_64',
      concurrency: { limit: 2, inFlight: 0 },
      capabilities: [
        capability('windows', 'os', 'Windows', 'ver', now),
        capability('dotnet:10.0.100', 'dotnet', '.NET SDK', 'dotnet --list-sdks', now, '10.0.100'),
      ],
    },
  ];

  return {
    agents,
    waiting: [
      {
        requestId: 'req-survey',
        title: 'Hold photos until the tablet is back on wifi',
        requires: ['macos', 'xcode:16'],
        eligibleAgentIds: ['agent-mac-01'],
      },
      {
        requestId: 'req-logger',
        title: 'Report battery voltage with each reading',
        requires: ['linux', 'toolchain:arm-none-eabi', 'usb_device:stm32f4'],
        // bench-pi has every capability but is on the wrong protocol version, so nothing can claim
        // this — which is exactly the case §33.6 says must produce a clear message rather than a
        // session that mysteriously never starts.
        eligibleAgentIds: [],
        queuedReason:
          'bench-pi has the toolchain and the STM32 attached, but it is running agent 0.3.9 and will not claim work until it is upgraded.',
      },
      {
        requestId: 'req-tariff',
        title: 'Try the new tariff calculator against last month',
        requires: ['linux', 'dotnet:10'],
        eligibleAgentIds: ['agent-linux-01'],
      },
    ],
  };
}

/**
 * §33.3 step 2. The command is assembled server-side so `--server` carries the instance's real base
 * URL — the browser's origin is not necessarily reachable from the machine the agent runs on.
 */
export function makePairingToken(now: number): { token: string; command: string; expiresAt: string } {
  const token = `cpt_${Math.random().toString(36).slice(2, 10)}${Math.random().toString(36).slice(2, 10)}`;
  return {
    token,
    command: `charter-agent --server https://charter.northbeam.example --token ${token}`,
    expiresAt: iso(15 * MINUTE, now),
  };
}
