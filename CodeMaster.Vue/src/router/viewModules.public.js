export const modules = import.meta.glob([
  '/src/views/**/*.vue',
  '!/src/views/codegen/projectPage/**'
])
