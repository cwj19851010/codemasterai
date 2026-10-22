<template>
  <div class="dashboard-container">
    <el-card class="welcome-card" shadow="never">
      <div class="welcome-content">
        <div>
          <p class="eyebrow">WELCOME</p>
          <h1>{{ t('app_title') }}</h1>
          <p class="summary">系统基础框架已就绪，业务模块会在配置并生成后显示在左侧菜单中。</p>
        </div>
        <el-icon class="welcome-icon"><HomeFilled /></el-icon>
      </div>
    </el-card>

    <el-row :gutter="16">
      <el-col v-for="item in capabilities" :key="item.title" :xs="24" :md="8">
        <el-card class="capability-card" shadow="hover">
          <el-icon :class="['capability-icon', item.tone]"><component :is="item.icon" /></el-icon>
          <div>
            <h2>{{ item.title }}</h2>
            <p>{{ item.description }}</p>
          </div>
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup>
import { DataAnalysis, HomeFilled, Lock, Monitor } from '@element-plus/icons-vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const capabilities = [
  { title: '权限与组织', description: '用户、角色、部门和菜单等基础管理能力。', icon: Lock, tone: 'primary' },
  { title: '运行监控', description: '登录日志、操作日志和任务运行情况。', icon: Monitor, tone: 'success' },
  { title: '业务扩展', description: '按当前项目需求逐步增加业务模块和报表。', icon: DataAnalysis, tone: 'warning' }
]
</script>

<style scoped lang="scss">
.dashboard-container {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.welcome-card {
  border: 0;
  color: #fff;
  background: linear-gradient(135deg, #2563eb, #4f46e5);
}

.welcome-content {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 24px;
  min-height: 150px;
  padding: 12px 8px;

  h1 {
    margin: 6px 0 12px;
    font-size: 30px;
  }
}

.eyebrow {
  margin: 0;
  opacity: 0.72;
  font-size: 12px;
  letter-spacing: 0.16em;
}

.summary {
  margin: 0;
  opacity: 0.9;
  line-height: 1.8;
}

.welcome-icon {
  flex: none;
  font-size: 72px;
  opacity: 0.22;
}

.capability-card {
  height: 100%;

  :deep(.el-card__body) {
    display: flex;
    align-items: flex-start;
    gap: 14px;
  }

  h2 {
    margin: 1px 0 8px;
    font-size: 16px;
  }

  p {
    margin: 0;
    color: var(--el-text-color-secondary);
    line-height: 1.7;
  }
}

.capability-icon {
  flex: none;
  padding: 10px;
  border-radius: 10px;
  font-size: 24px;

  &.primary { color: #2563eb; background: #eff6ff; }
  &.success { color: #059669; background: #ecfdf5; }
  &.warning { color: #d97706; background: #fffbeb; }
}

@media (max-width: 768px) {
  .welcome-content h1 { font-size: 24px; }
  .welcome-icon { display: none; }
}
</style>
