import { defineConfig } from 'vitepress'

// Published at https://binn.github.io/Charter/, so every asset needs the repository
// name as a base path. Change this if the site ever moves to a custom domain.
const base = '/Charter/'

const repo = 'https://github.com/binn/Charter'

export default defineConfig({
  title: 'Charter',
  description: 'Charter your projects. Self-hosted feature requests, refined into specs, built by coding agents, returned as a preview link.',
  lang: 'en-GB',
  base,

  // Dead links are a documentation bug. Fix the link rather than relaxing this.
  ignoreDeadLinks: false,

  lastUpdated: true,
  cleanUrls: true,

  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: `${base}favicon.svg` }],
    ['meta', { name: 'theme-color', content: '#1E6B5E' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:site_name', content: 'Charter' }],
    ['meta', { property: 'og:title', content: 'Charter documentation' }],
    ['meta', { property: 'og:description', content: 'Charter your projects. Self-hosted, pre-1.0, AGPL-3.0.' }]
  ],

  markdown: {
    // Shiki ships no `gitignore` grammar and charter-folder.md fences one. Aliasing to
    // bash highlights the `#` comments correctly and keeps the build warning-free,
    // without editing the page. Note the alias target must be a grammar Shiki loads:
    // pointing this at `txt` turns the warning into a hard build failure.
    languageAlias: {
      gitignore: 'bash'
    }
  },

  themeConfig: {
    // The mark renders as an <img>, so `currentColor` would not inherit the theme.
    // Two files instead, one per appearance.
    logo: {
      light: '/charter-mark.svg',
      dark: '/charter-mark-dark.svg',
      alt: ''
    },
    siteTitle: 'Charter',

    nav: [
      { text: 'Getting started', link: '/getting-started', activeMatch: '^/(getting-started|the-loop|self-hosting|configuration)' },
      {
        text: 'Operating',
        activeMatch: '^/(runners|credentials|budgets|upgrading|privacy)',
        items: [
          { text: 'Runners', link: '/runners' },
          { text: 'Model credentials', link: '/credentials' },
          { text: 'Budgets', link: '/budgets' },
          { text: 'Upgrading', link: '/upgrading' },
          { text: 'Privacy', link: '/privacy' }
        ]
      },
      {
        text: 'Reference',
        activeMatch: '^/(api|adapters|charter-folder|standards)',
        items: [
          { text: 'HTTP API', link: '/api' },
          { text: 'Agent adapters', link: '/adapters' },
          { text: 'The .charter/ folder', link: '/charter-folder' },
          { text: 'Organisation standards', link: '/standards' }
        ]
      },
      { text: 'Security', link: '/security', activeMatch: '^/security' },
      {
        text: 'Project',
        items: [
          { text: 'Changelog', link: `${repo}/blob/master/CHANGELOG.md` },
          { text: 'Contributing', link: `${repo}/blob/master/CONTRIBUTING.md` },
          { text: 'Specification', link: `${repo}/blob/master/agent-docs/spec.md` },
          { text: 'Report a vulnerability', link: `${repo}/blob/master/SECURITY.md` }
        ]
      }
    ],

    // Every page in docs/ appears here. An orphaned page is invisible to readers
    // even though the file exists — see AGENTS.md.
    sidebar: [
      {
        text: 'Getting started',
        collapsed: false,
        items: [
          { text: 'First instance', link: '/getting-started' },
          { text: 'The loop', link: '/the-loop' },
          { text: 'Self-hosting', link: '/self-hosting' },
          { text: 'Configuration', link: '/configuration' }
        ]
      },
      {
        text: 'Operating',
        collapsed: false,
        items: [
          { text: 'Runners', link: '/runners' },
          { text: 'Model credentials', link: '/credentials' },
          { text: 'Budgets', link: '/budgets' },
          { text: 'Upgrading', link: '/upgrading' },
          { text: 'Privacy', link: '/privacy' }
        ]
      },
      {
        text: 'Reference',
        collapsed: false,
        items: [
          { text: 'HTTP API', link: '/api' },
          { text: 'Agent adapters', link: '/adapters' },
          { text: 'The .charter/ folder', link: '/charter-folder' },
          { text: 'Organisation standards', link: '/standards' }
        ]
      },
      {
        text: 'Security',
        collapsed: false,
        items: [{ text: 'Threat model', link: '/security' }]
      }
    ],

    outline: { level: [2, 3], label: 'On this page' },

    search: {
      provider: 'local',
      options: {
        detailedView: true
      }
    },

    socialLinks: [{ icon: 'github', link: repo }],

    editLink: {
      pattern: `${repo}/edit/master/docs/:path`,
      text: 'Edit this page on GitHub'
    },

    docFooter: {
      prev: 'Previous',
      next: 'Next'
    },

    footer: {
      message: `Released under the <a href="${repo}/blob/master/LICENSE">AGPL-3.0-only</a> licence. "Charter" and the Charter mark are <a href="${repo}/blob/master/TRADEMARK.md">not covered by that licence</a>.`,
      copyright: `<a href="${repo}">Source on GitHub</a>`
    }
  }
})
