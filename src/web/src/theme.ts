import { createDarkTheme, type BrandVariants, type Theme } from '@fluentui/react-components'

/**
 * The console's look: dark slate surfaces, Nunito Sans with IBM Plex Mono labels, tight radii, and an orange accent.
 * Fluent components read these tokens; suite.css styles the shell with the same values as CSS variables.
 */
export const SUITE = {
  accent: '#fb923c',
  accentContrast: '#1c0d02',
  bgDeep: '#0e1014',
  bg: '#151821',
  bgElevated: '#1c2030',
  bgHover: '#272d40',
  border: '#363d52',
  text: '#e8ebf2',
  textMuted: '#9399ad',
  danger: '#e85d5d',
  warning: '#d4a017',
  success: '#3fb97f',
  fontBody: '"Nunito Sans", system-ui, sans-serif',
  fontMono: '"IBM Plex Mono", ui-monospace, monospace',
}

/** Mixes two #rrggbb colors (weight = share of `a`). */
export function mix(a: string, b: string, weight: number): string {
  const p = (h: string) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16))
  const [x, y] = [p(a), p(b)]
  return '#' + x.map((v, i) => Math.round(v * weight + y[i] * (1 - weight)).toString(16).padStart(2, '0')).join('')
}

/** A Fluent brand ramp (10 darkest … 160 lightest) through the accent. */
function brandRamp(accent: string): BrandVariants {
  const ramp: Record<number, string> = {}
  for (let step = 10; step <= 160; step += 10) {
    ramp[step] = step <= 90 ? mix(accent, '#000000', 0.15 + (step / 90) * 0.85) : mix(accent, '#ffffff', 1 - ((step - 90) / 70) * 0.85)
  }
  return ramp as unknown as BrandVariants
}

export function suiteTheme(): Theme {
  const s = SUITE
  const base = createDarkTheme(brandRamp(s.accent))
  return {
    ...base,
    fontFamilyBase: s.fontBody,
    fontFamilyMonospace: s.fontMono,
    fontFamilyNumeric: s.fontBody,
    borderRadiusSmall: '2px',
    borderRadiusMedium: '4px',
    borderRadiusLarge: '6px',
    borderRadiusXLarge: '6px',

    // Surfaces: canvas (deep) → panels (bg) → raised chrome (elevated) → hover.
    colorNeutralBackground1: s.bg,
    colorNeutralBackground1Hover: s.bgHover,
    colorNeutralBackground1Pressed: mix(s.bgHover, s.bg, 0.6),
    colorNeutralBackground1Selected: s.bgHover,
    colorNeutralBackground2: s.bgElevated,
    colorNeutralBackground2Hover: s.bgHover,
    colorNeutralBackground2Pressed: mix(s.bgHover, s.bgElevated, 0.6),
    colorNeutralBackground2Selected: s.bgHover,
    colorNeutralBackground3: s.bgDeep,
    colorNeutralBackground3Hover: s.bg,
    colorNeutralBackground3Selected: s.bg,
    colorNeutralBackground4: s.bgDeep,
    colorNeutralBackground5: s.bgDeep,
    colorNeutralBackground6: s.bgElevated,
    colorNeutralCardBackground: s.bg,
    colorNeutralCardBackgroundHover: s.bgHover,
    colorSubtleBackgroundHover: s.bgHover,
    colorSubtleBackgroundPressed: mix(s.bgHover, s.bg, 0.6),
    colorSubtleBackgroundSelected: s.bgHover,
    colorNeutralBackgroundDisabled: s.bgElevated,

    colorNeutralStroke1: s.border,
    colorNeutralStroke1Hover: mix(s.border, s.textMuted, 0.7),
    colorNeutralStroke1Pressed: mix(s.border, s.textMuted, 0.6),
    colorNeutralStroke2: mix(s.border, s.bg, 0.75),
    colorNeutralStroke3: mix(s.border, s.bg, 0.55),
    colorNeutralStrokeAccessible: s.textMuted,
    colorNeutralStrokeAccessibleHover: s.text,

    colorNeutralForeground1: s.text,
    colorNeutralForeground1Hover: s.text,
    colorNeutralForeground2: mix(s.text, s.textMuted, 0.55),
    colorNeutralForeground2Hover: s.text,
    colorNeutralForeground3: s.textMuted,
    colorNeutralForeground3Hover: s.text,
    colorNeutralForeground4: mix(s.textMuted, s.bg, 0.8),
    colorNeutralForegroundDisabled: mix(s.textMuted, s.bg, 0.55),

    // Accent: primary buttons are accent with dark text, as in the suite.
    colorBrandBackground: s.accent,
    colorBrandBackgroundHover: mix(s.accent, '#ffffff', 0.85),
    colorBrandBackgroundPressed: mix(s.accent, '#000000', 0.85),
    colorBrandBackgroundSelected: mix(s.accent, '#000000', 0.9),
    colorNeutralForegroundOnBrand: s.accentContrast,
    colorBrandForeground1: s.accent,
    colorBrandForeground2: mix(s.accent, '#ffffff', 0.8),
    colorBrandForegroundLink: s.accent,
    colorBrandForegroundLinkHover: mix(s.accent, '#ffffff', 0.8),
    colorBrandStroke1: s.accent,
    colorCompoundBrandForeground1: s.accent,
    colorCompoundBrandForeground1Hover: mix(s.accent, '#ffffff', 0.85),
    colorCompoundBrandBackground: s.accent,
    colorCompoundBrandBackgroundHover: mix(s.accent, '#ffffff', 0.85),
    colorCompoundBrandStroke: s.accent,
    colorCompoundBrandStrokeHover: mix(s.accent, '#ffffff', 0.85),
    colorBrandBackground2: mix(s.accent, s.bg, 0.12),
    colorStrokeFocus2: s.accent,

    colorPaletteRedForeground1: s.danger,
    colorPaletteRedBackground3: s.danger,
    colorPaletteRedBorder2: s.danger,
    colorPaletteGreenForeground1: s.success,
    colorPaletteGreenBackground3: s.success,
    colorPaletteYellowForeground1: s.warning,
    colorStatusDangerForeground1: s.danger,
    colorStatusDangerBackground1: mix(s.danger, s.bg, 0.12),
    colorStatusDangerBorder1: mix(s.danger, s.bg, 0.35),
    colorStatusWarningForeground1: s.warning,
    colorStatusWarningBackground1: mix(s.warning, s.bg, 0.12),
    colorStatusWarningBorder1: mix(s.warning, s.bg, 0.35),
    colorStatusSuccessForeground1: s.success,
    colorStatusSuccessBackground1: mix(s.success, s.bg, 0.12),
    colorStatusSuccessBorder1: mix(s.success, s.bg, 0.35),
  }
}
