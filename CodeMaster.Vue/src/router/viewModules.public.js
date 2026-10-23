// 公共版视图清单。
//
// 2026-10-23 之前这里排除 /src/views/codegen/projectPage/**（复杂页设计器）。
// 复杂页开源后，公共版与全量版的视图集合一致；保留这个独立文件是为了将来再出现
// 闭源视图时，只改这里而不必动 vite.config.js 的别名解析。
export const modules = import.meta.glob('/src/views/**/*.vue')
