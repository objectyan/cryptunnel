## 变更内容

<!-- 一句话说清楚改了什么 -->

## 变更类型

- [ ] 修复缺陷
- [ ] 新功能
- [ ] 重构（无外部行为变化）
- [ ] 文档
- [ ] 构建 / CI

## 是否触碰兼容性红线

<!-- 红线清单见 CONTRIBUTING.md：线级传输格式、密钥派生、AUTH 报文格式、WS 路径、HTTP 降级端点、分片大小 -->

- [ ] 否
- [ ] 是 —— 请在下方说明原因、影响范围与迁移方式

## 自测

- [ ] `cd cryptunnel-core && mvn clean install` 通过
- [ ] `mvn -Pboot25 clean install` 通过
- [ ] `mvn -Pjakarta clean install` 通过
- [ ] 单元测试全部通过，且已覆盖本次改动
- [ ] 未使用 JDK 9+ API（core 编译目标为 1.8）

## 相关文档

<!-- 涉及的 ADR 编号、Issue 链接；如涉及架构方向变更请先附 ADR -->
