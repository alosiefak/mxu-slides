#!/usr/bin/env node
// Builds the Windows app icon (AppIcon.ico) from the Mac app's icon PNGs, so
// both apps share one source of truth. ICO files may embed PNG images
// directly, which keeps this dependency-free.
//
// Usage: node apps/windows/tools/make-icon.mjs

import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const iconSet = join(here, '..', '..', 'mac', 'Assets.xcassets', 'AppIcon.appiconset')
const out = join(here, '..', 'src', 'MxU.Slides.Windows', 'Assets', 'AppIcon.ico')

const sources = [
  [16, 'icon_16x16.png'],
  [24, 'icon_16x16@2x.png'], // 32px source; Windows scales it for the 24px slot
  [32, 'icon_32x32.png'],
  [48, 'icon_32x32@2x.png'],
  [64, 'icon_32x32@2x.png'],
  [128, 'icon_128x128.png'],
  [256, 'icon_256x256.png'],
]

const images = sources.map(([size, file]) => ({ size, png: readFileSync(join(iconSet, file)) }))
const header = Buffer.alloc(6)
header.writeUInt16LE(0, 0)
header.writeUInt16LE(1, 2)
header.writeUInt16LE(images.length, 4)

let offset = 6 + 16 * images.length
const entries = images.map(({ size, png }) => {
  const entry = Buffer.alloc(16)
  entry.writeUInt8(size >= 256 ? 0 : size, 0)
  entry.writeUInt8(size >= 256 ? 0 : size, 1)
  entry.writeUInt8(0, 2)
  entry.writeUInt8(0, 3)
  entry.writeUInt16LE(1, 4)
  entry.writeUInt16LE(32, 6)
  entry.writeUInt32LE(png.length, 8)
  entry.writeUInt32LE(offset, 12)
  offset += png.length
  return entry
})

writeFileSync(out, Buffer.concat([header, ...entries, ...images.map((i) => i.png)]))
console.log(`Wrote ${out}`)
