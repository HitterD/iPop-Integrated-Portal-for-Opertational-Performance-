# Regenerates src/IPop.Web/wwwroot/css/core/tailwind.css (Tailwind 3.4.17, the version the Play CDN served).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here '..\..\src\IPop.Web\wwwroot\css\core\tailwind.css'
npx --yes tailwindcss@3.4.17 -c (Join-Path $here 'tailwind.config.cjs') -i (Join-Path $here 'input.css') -o $out --minify
