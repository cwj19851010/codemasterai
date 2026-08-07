<template>
  <div class="dashboard-container">
    <section class="dashboard-hero">
      <div class="hero-decor" aria-hidden="true"></div>
      <div>
        <p class="eyebrow">CodeMasterAI · 数据驱动代码生成</p>
        <h1>以数据为驱动，生成全场景代码</h1>
        <p class="hero-copy">
          定义好实体、字段和模板，剩下的交给生成器——复杂场景全量生成，迭代场景增量更新，手改内容不丢失。
        </p>
      </div>
      <div class="hero-status">
        <div class="mode-badge">
          <el-icon><FullScreen /></el-icon>
          <span>全量生成</span>
        </div>
        <div class="mode-badge">
          <el-icon><Refresh /></el-icon>
          <span>增量生成</span>
        </div>
      </div>
    </section>

    <div class="stat-strip">
      <div v-for="stat in stats" :key="stat.title">
        <strong>{{ stat.title }}</strong>
        <span>{{ stat.desc }}</span>
      </div>
    </div>

    <el-card shadow="never" class="start-card">
      <div>
        <strong>开始使用</strong>
        <span>新建项目 → 设计实体和字段 → 生成代码 → 构建验证，一条链路走完。</span>
      </div>
      <el-button type="primary" size="large" @click="goProjects">前往项目管理</el-button>
    </el-card>
  </div>
</template>

<script setup>
import { FullScreen, Refresh } from '@element-plus/icons-vue'
import { useRouter } from 'vue-router'

const router = useRouter()

const goProjects = () => {
  router.push('/codegen/project')
}

const stats = [
  { title: '数据建模', desc: '实体 · 字段 · 关系' },
  { title: '模板库', desc: '页面 · 控件 · 子表' },
  { title: '全量生成', desc: '复杂场景一次成型' },
  { title: '增量生成', desc: '按字段更新 · 保留手改' }
]
</script>

<style scoped lang="scss">
.dashboard-container {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.dashboard-hero {
  position: relative;
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 24px;
  padding: 34px 32px;
  overflow: hidden;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 8px;
  background:
    linear-gradient(135deg, rgba(var(--app-primary-rgb, 79, 70, 229), 0.12), transparent 40%),
    linear-gradient(135deg, rgba(5, 150, 105, 0.06), transparent 64%),
    linear-gradient(135deg, var(--app-surface, #fff), var(--app-surface-soft, #f8fafc));
  box-shadow: var(--app-card-shadow, 0 10px 30px rgba(31, 41, 55, 0.08));

  h1 {
    margin: 4px 0 10px;
    color: var(--app-text, #1f2937);
    font-size: 30px;
    font-weight: 700;
  }
}

.hero-decor {
  position: absolute;
  top: -80px;
  right: 160px;
  width: 280px;
  height: 280px;
  pointer-events: none;
  opacity: 0.5;
  background-image: radial-gradient(circle, rgba(var(--app-primary-rgb, 79, 70, 229), 0.5) 1px, transparent 1px);
  background-size: 22px 22px;
  mask-image: linear-gradient(135deg, rgba(0, 0, 0, 0.9), transparent 72%);
  -webkit-mask-image: linear-gradient(135deg, rgba(0, 0, 0, 0.9), transparent 72%);
}

.eyebrow {
  margin: 0;
  color: var(--app-primary, #4f46e5);
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.04em;
}

.hero-copy {
  max-width: 720px;
  margin: 0;
  color: var(--app-text-muted, #64748b);
  line-height: 1.7;
}

.hero-status {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
}

.mode-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  border: 1px solid var(--app-border, #dbe4ef);
  border-radius: 8px;
  color: var(--app-accent, #059669);
  background: var(--app-surface, #fff);
  font-weight: 600;
  white-space: nowrap;

  &:first-child {
    color: var(--app-primary, #4f46e5);
  }
}

.stat-strip {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;

  div {
    padding: 16px 18px;
    border: 1px solid var(--app-border, #dbe4ef);
    border-radius: 8px;
    background: var(--app-surface, #fff);
    box-shadow: var(--app-card-shadow, 0 10px 30px rgba(31, 41, 55, 0.08));
  }

  strong,
  span {
    display: block;
  }

  strong {
    color: var(--app-text, #1f2937);
    font-size: 15px;
  }

  span {
    margin-top: 6px;
    color: var(--app-text-muted, #64748b);
    font-size: 12px;
  }
}

.start-card :deep(.el-card__body) {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;

  strong,
  span {
    display: block;
  }

  strong {
    color: var(--app-text, #1f2937);
    font-size: 16px;
  }

  span {
    margin-top: 6px;
    color: var(--app-text-muted, #64748b);
  }
}

@media (max-width: 1100px) {
  .stat-strip {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 720px) {
  .dashboard-hero {
    align-items: flex-start;
    flex-direction: column;
  }

  .stat-strip {
    grid-template-columns: 1fr;
  }

  .hero-decor {
    display: none;
  }

  .start-card :deep(.el-card__body) {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
