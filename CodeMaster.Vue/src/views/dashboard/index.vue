<template>
  <div class="dashboard-container">
    <section class="dashboard-hero">
      <div class="hero-decor" aria-hidden="true"></div>
      <div class="hero-grid-lines" aria-hidden="true"></div>
      <div class="hero-glow" aria-hidden="true"></div>

      <div class="hero-main">
        <p class="eyebrow">
          <span class="eyebrow-dot"></span>
          CodeMasterAI · 数据驱动代码生成
        </p>
        <h1>以数据为驱动，生成全场景代码</h1>
        <p class="hero-copy">
          定义好实体、字段和模板，剩下的交给生成器——复杂场景全量生成，迭代场景增量更新，手改内容不丢失。
        </p>
        <div class="hero-actions">
          <el-button type="primary" size="large" class="hero-cta" @click="goProjects">
            <el-icon><Promotion /></el-icon>
            <span>立即开始建模</span>
          </el-button>
          <el-button size="large" class="hero-ghost" @click="scrollTo('pipeline')">
            <el-icon><VideoPlay /></el-icon>
            <span>查看生成流水线</span>
          </el-button>
        </div>
      </div>

      <div class="hero-status">
        <div class="mode-card glass-card">
          <div class="mode-icon">
            <el-icon><FullScreen /></el-icon>
          </div>
          <div>
            <strong>全量生成</strong>
            <span>复杂场景一次成型</span>
          </div>
        </div>
        <div class="mode-card glass-card">
          <div class="mode-icon">
            <el-icon><Refresh /></el-icon>
          </div>
          <div>
            <strong>增量生成</strong>
            <span>按字段更新 · 保留手改</span>
          </div>
        </div>
      </div>
    </section>

    <section class="stat-grid">
      <div v-for="(stat, i) in stats" :key="stat.title" class="stat-card glass-card" :style="{ '--delay': `${i * 70}ms` }">
        <div class="stat-icon" :class="`tone-${i % 4}`">
          <el-icon><component :is="stat.icon" /></el-icon>
        </div>
        <div class="stat-body">
          <strong>{{ stat.title }}</strong>
          <span>{{ stat.desc }}</span>
        </div>
        <span class="stat-index" aria-hidden="true">0{{ i + 1 }}</span>
      </div>
    </section>

    <section class="pipeline" id="pipeline">
      <header class="section-head">
        <h2>一条链路，走完交付</h2>
        <p>从建模到运行，四步直达，全程可视化。</p>
      </header>
      <div class="pipeline-steps">
        <div v-for="(step, i) in steps" :key="step.title" class="step-card glass-card" :style="{ '--delay': `${i * 90}ms` }">
          <div class="step-no">{{ String(i + 1).padStart(2, '0') }}</div>
          <div class="step-icon" :class="`tone-${i % 4}`">
            <el-icon><component :is="step.icon" /></el-icon>
          </div>
          <strong>{{ step.title }}</strong>
          <span>{{ step.desc }}</span>
        </div>
      </div>
    </section>

    <section class="start-card glass-card">
      <div class="start-glow" aria-hidden="true"></div>
      <div class="start-body">
        <strong>开始使用</strong>
        <span>新建项目 → 设计实体和字段 → 生成代码 → 构建验证，一条链路走完。</span>
      </div>
      <el-button type="primary" size="large" class="start-btn" @click="goProjects">
        <span>前往项目管理</span>
        <el-icon><ArrowRight /></el-icon>
      </el-button>
    </section>
  </div>
</template>

<script setup>
import { FullScreen, Refresh, Promotion, VideoPlay, ArrowRight, Grid, Collection, MagicStick, Files, Memo, Operation, Connection } from '@element-plus/icons-vue'
import { useRouter } from 'vue-router'

const router = useRouter()

const goProjects = () => {
  router.push('/codegen/project')
}

const scrollTo = (id) => {
  document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

const stats = [
  { title: '数据建模', desc: '实体 · 字段 · 关系', icon: Grid },
  { title: '模板库', desc: '页面 · 控件 · 子表', icon: Collection },
  { title: '全量生成', desc: '复杂场景一次成型', icon: MagicStick },
  { title: '增量生成', desc: '按字段更新 · 保留手改', icon: Refresh }
]

const steps = [
  { title: '创建项目', desc: '选择数据库，初始化工程骨架', icon: Memo },
  { title: '设计实体', desc: '可视化配置字段、关系与模板', icon: Operation },
  { title: '生成代码', desc: '全量或增量输出页面与接口', icon: MagicStick },
  { title: '构建验证', desc: '启动前后端，跑通真实链路', icon: Connection }
]
</script>

<style scoped lang="scss">
.dashboard-container {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

/* ---------- shared glass ---------- */
.glass-card {
  position: relative;
  border: 1px solid var(--app-border, #dbe4ef);
  background: var(--app-surface, #fff);
  box-shadow: var(--app-card-shadow, 0 10px 30px rgba(31, 41, 55, 0.08));
  transition: transform 0.22s cubic-bezier(0.2, 0.8, 0.3, 1), box-shadow 0.22s ease, border-color 0.22s ease;

  &:hover {
    transform: translateY(-3px);
    border-color: rgba(var(--app-primary-rgb, 79, 70, 229), 0.35);
    box-shadow: 0 14px 34px rgba(var(--app-primary-rgb, 79, 70, 229), 0.14);
  }

  &:focus-visible {
    outline: 2px solid var(--app-primary, #4f46e5);
    outline-offset: 2px;
  }
}

/* ---------- hero ---------- */
.dashboard-hero {
  position: relative;
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 28px;
  padding: 40px 36px;
  overflow: hidden;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 14px;
  background:
    linear-gradient(135deg, rgba(var(--app-primary-rgb, 79, 70, 229), 0.14), transparent 42%),
    linear-gradient(135deg, rgba(var(--app-accent-rgb, 5, 150, 105), 0.08), transparent 62%),
    linear-gradient(135deg, var(--app-surface, #fff), var(--app-surface-soft, #f8fafc));
  box-shadow: var(--app-card-shadow, 0 10px 30px rgba(31, 41, 55, 0.08));

  h1 {
    margin: 8px 0 12px;
    color: var(--app-text, #1f2937);
    font-size: 32px;
    line-height: 1.25;
    font-weight: 800;
    letter-spacing: -0.01em;
  }
}

.hero-decor {
  position: absolute;
  top: -90px;
  right: 140px;
  width: 300px;
  height: 300px;
  pointer-events: none;
  opacity: 0.5;
  background-image: radial-gradient(circle, rgba(var(--app-primary-rgb, 79, 70, 229), 0.5) 1px, transparent 1px);
  background-size: 22px 22px;
  mask-image: linear-gradient(135deg, rgba(0, 0, 0, 0.9), transparent 72%);
  -webkit-mask-image: linear-gradient(135deg, rgba(0, 0, 0, 0.9), transparent 72%);
}

.hero-grid-lines {
  position: absolute;
  inset: 0;
  pointer-events: none;
  opacity: 0.5;
  background-image:
    linear-gradient(rgba(var(--app-primary-rgb, 79, 70, 229), 0.06) 1px, transparent 1px),
    linear-gradient(90deg, rgba(var(--app-primary-rgb, 79, 70, 229), 0.06) 1px, transparent 1px);
  background-size: 44px 44px;
  mask-image: radial-gradient(ellipse at 85% 0%, rgba(0, 0, 0, 0.7), transparent 58%);
  -webkit-mask-image: radial-gradient(ellipse at 85% 0%, rgba(0, 0, 0, 0.7), transparent 58%);
}

.hero-glow {
  position: absolute;
  top: -70px;
  right: -60px;
  width: 320px;
  height: 320px;
  border-radius: 50%;
  pointer-events: none;
  background: radial-gradient(circle, rgba(var(--app-primary-rgb, 79, 70, 229), 0.18), transparent 66%);
  filter: blur(30px);
}

.hero-main {
  position: relative;
  z-index: 1;
  max-width: 640px;
}

.eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  color: var(--app-primary, #4f46e5);
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.06em;
}

.eyebrow-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--app-primary, #4f46e5);
  box-shadow: 0 0 0 4px rgba(var(--app-primary-rgb, 79, 70, 229), 0.16);
}

.hero-copy {
  max-width: 560px;
  margin: 0 0 22px;
  color: var(--app-text-muted, #64748b);
  font-size: 15px;
  line-height: 1.8;
}

.hero-actions {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}

.hero-cta {
  border-radius: 10px;
  box-shadow: 0 10px 22px rgba(var(--app-primary-rgb, 79, 70, 229), 0.3);
  transition: transform 0.2s ease, box-shadow 0.2s ease;

  &:hover {
    transform: translateY(-2px);
    box-shadow: 0 14px 28px rgba(var(--app-primary-rgb, 79, 70, 229), 0.38);
  }
}

.hero-ghost {
  border-radius: 10px;
}

.hero-status {
  position: relative;
  z-index: 1;
  display: flex;
  flex-direction: column;
  gap: 12px;
  flex-shrink: 0;
}

.mode-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 18px;
  border-radius: 12px;

  strong,
  span {
    display: block;
  }

  strong {
    color: var(--app-text, #1f2937);
    font-size: 14px;
  }

  span {
    margin-top: 3px;
    color: var(--app-text-muted, #64748b);
    font-size: 12px;
  }
}

.mode-icon {
  display: grid;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 10px;
  color: var(--app-accent, #059669);
  background: rgba(var(--app-accent-rgb, 5, 150, 105), 0.1);
  font-size: 19px;

  &:first-child {
    color: var(--app-primary, #4f46e5);
    background: rgba(var(--app-primary-rgb, 79, 70, 229), 0.1);
  }
}

/* ---------- stat grid (bento) ---------- */
.stat-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;
}

.stat-card {
  display: flex;
  align-items: center;
  gap: 13px;
  padding: 18px;
  border-radius: 12px;
  animation: rise-in 0.5s cubic-bezier(0.2, 0.8, 0.3, 1) both;
  animation-delay: var(--delay, 0ms);
}

.stat-icon {
  display: grid;
  place-items: center;
  width: 42px;
  height: 42px;
  border-radius: 11px;
  flex-shrink: 0;
  font-size: 21px;
}

.tone-0 { color: var(--app-primary, #4f46e5); background: rgba(var(--app-primary-rgb, 79, 70, 229), 0.1); }
.tone-1 { color: var(--app-accent, #059669); background: rgba(var(--app-accent-rgb, 5, 150, 105), 0.1); }
.tone-2 { color: #d97706; background: rgba(217, 119, 6, 0.12); }
.tone-3 { color: #0891b2; background: rgba(8, 145, 178, 0.12); }

.stat-body {
  min-width: 0;

  strong,
  span {
    display: block;
  }

  strong {
    color: var(--app-text, #1f2937);
    font-size: 15px;
  }

  span {
    margin-top: 4px;
    color: var(--app-text-muted, #64748b);
    font-size: 12px;
    line-height: 1.5;
  }
}

.stat-index {
  position: absolute;
  top: 8px;
  right: 12px;
  color: rgba(var(--app-primary-rgb, 79, 70, 229), 0.18);
  font-size: 22px;
  font-weight: 800;
  font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', monospace;
  line-height: 1;
}

/* ---------- pipeline ---------- */
.pipeline {
  scroll-margin-top: 80px;
}

.section-head {
  margin-bottom: 14px;

  h2 {
    margin: 0;
    color: var(--app-text, #1f2937);
    font-size: 18px;
    font-weight: 700;
  }

  p {
    margin: 6px 0 0;
    color: var(--app-text-muted, #64748b);
    font-size: 13px;
  }
}

.pipeline-steps {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;
}

.step-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
  padding: 20px 18px;
  border-radius: 12px;
  animation: rise-in 0.5s cubic-bezier(0.2, 0.8, 0.3, 1) both;
  animation-delay: var(--delay, 0ms);

  strong {
    color: var(--app-text, #1f2937);
    font-size: 15px;
  }

  span {
    color: var(--app-text-muted, #64748b);
    font-size: 12px;
    line-height: 1.6;
  }
}

.step-no {
  position: absolute;
  top: 12px;
  right: 14px;
  color: var(--app-text-muted, #94a3b8);
  opacity: 0.55;
  font-size: 13px;
  font-weight: 700;
  font-family: 'SFMono-Regular', Consolas, 'Liberation Mono', monospace;
}

.step-icon {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  border-radius: 11px;
  margin-bottom: 2px;
  font-size: 20px;
}

/* ---------- start card ---------- */
.start-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  padding: 22px 26px;
  border-radius: 14px;
  overflow: hidden;

  &:hover {
    border-color: rgba(var(--app-primary-rgb, 79, 70, 229), 0.4);
  }
}

.start-glow {
  position: absolute;
  top: -60px;
  right: -40px;
  width: 220px;
  height: 220px;
  border-radius: 50%;
  pointer-events: none;
  background: radial-gradient(circle, rgba(var(--app-primary-rgb, 79, 70, 229), 0.12), transparent 66%);
}

.start-body {
  position: relative;
  z-index: 1;

  strong,
  span {
    display: block;
  }

  strong {
    color: var(--app-text, #1f2937);
    font-size: 17px;
  }

  span {
    margin-top: 6px;
    color: var(--app-text-muted, #64748b);
    font-size: 13px;
  }
}

.start-btn {
  position: relative;
  z-index: 1;
  flex-shrink: 0;
  border-radius: 10px;
  box-shadow: 0 10px 22px rgba(var(--app-primary-rgb, 79, 70, 229), 0.28);
  transition: transform 0.2s ease, box-shadow 0.2s ease;

  &:hover {
    transform: translateY(-2px);
    box-shadow: 0 14px 28px rgba(var(--app-primary-rgb, 79, 70, 229), 0.36);
  }
}

/* ---------- entrance animation ---------- */
@keyframes rise-in {
  from {
    opacity: 0;
    transform: translateY(14px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

@media (prefers-reduced-motion: reduce) {
  .stat-card,
  .step-card {
    animation: none;
  }

  .glass-card,
  .hero-cta,
  .start-btn {
    transition: none;
  }
}

/* ---------- responsive ---------- */
@media (max-width: 1100px) {
  .stat-grid,
  .pipeline-steps {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 720px) {
  .dashboard-hero {
    align-items: flex-start;
    flex-direction: column;
    padding: 28px 22px;
  }

  .hero-status {
    flex-direction: row;
    flex-wrap: wrap;
  }

  .stat-grid,
  .pipeline-steps {
    grid-template-columns: 1fr;
  }

  .hero-decor,
  .hero-grid-lines,
  .hero-glow {
    display: none;
  }

  .start-card {
    flex-direction: column;
    align-items: flex-start;
    padding: 20px 22px;
  }
}
</style>
