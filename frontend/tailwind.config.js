/** @type {import('tailwindcss').Config} */
export default {
  content: [
    './index.html',
    './src/**/*.{js,ts,jsx,tsx}',
    './node_modules/@tremor/**/*.{js,ts,jsx,tsx}',
  ],
  // Tremor's LineChart applies stroke/fill colors as dynamic classes (e.g. `stroke-red-500`)
  // built at runtime from the `colors` prop. Tailwind's content scan never sees those strings,
  // so without a safelist the classes get purged and chart lines render with no color.
  safelist: [
    'stroke-red-500',
    'stroke-blue-500',
    'stroke-amber-500',
    'fill-red-500',
    'fill-blue-500',
    'fill-amber-500',
    'dark:stroke-red-500',
    'dark:stroke-blue-500',
    'dark:stroke-amber-500',
    'dark:fill-red-500',
    'dark:fill-blue-500',
    'dark:fill-amber-500',
  ],
  theme: {
    extend: {
      // Tailwind 3 ships only `current`/`transparent`/`none` for stroke and fill by default,
      // so `stroke-red-500` etc. produce no rule even if the class is in safelist. Map the
      // full color palette so Tremor's chart line colors actually render.
      stroke: ({ theme }) => theme('colors'),
      fill: ({ theme }) => theme('colors'),
    },
  },
  plugins: [],
};
