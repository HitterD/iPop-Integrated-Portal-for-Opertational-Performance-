// Build-time replacement for the Tailwind Play CDN that index.html used to load (same version, same config).
// Regenerate src/IPop.Web/wwwroot/css/core/tailwind.css with tools/tailwind/build.ps1 after adding utility classes.
const path = require('path');
const web = path.resolve(__dirname, '../../src/IPop.Web');
module.exports = {
  content: [
    path.join(web, '**/*.razor'),
    path.join(web, '**/*.razor.cs'),
    path.join(web, '**/*.cs'),
    path.join(web, 'wwwroot/index.html'),
    path.join(web, 'wwwroot/js/**/*.js'),
    '!' + path.join(web, 'wwwroot/js/livekit-client.umd.js'),
    '!' + path.join(web, 'wwwroot/js/lucide.min.js'),
    '!' + path.join(web, 'bin/**'),
    '!' + path.join(web, 'obj/**'),
  ],
  // The CDN only generated classes present in the live DOM; scanning source also picks up words like "container"
  // (no element uses Tailwind's .container), whose 640-1536 px max-width steps the phone-breakpoint rule forbids.
  corePlugins: { container: false },
  theme: {
    extend: {
      colors: {
        accent: {
          50: '#f0f9f4', 100: '#dcf2e5', 200: '#bce5d0', 300: '#8dd1b0', 400: '#56b689', 500: '#2e9d6b',
          600: '#228055', 700: '#1d6646', 800: '#1a523a', 900: '#164431', 950: '#0b261c',
        },
      },
      fontFamily: {
        sans: ['Inter', '-apple-system', 'BlinkMacSystemFont', 'Segoe UI', 'system-ui', 'sans-serif'],
      },
    },
  },
};
