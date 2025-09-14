# Renderloom MobileGpuTerrain

Mobile-first GPU terrain: gpu quad-tree culling, shader seamless stitching, clipmap streaming, SplatID Blend, and RVT
## 技术点
-  gpu quad-tree culling &vertex shader  stitching  逆水寒手游地形 like 
-  SplatID Blend - 存多份数据，四边形双线性插值，可以往三角洲重心坐标插值优化
-  clipmap streaming,  height map   SplatID 都支持了clipmap，减少内存，提高初始化速度及远处gpu 缓存命中 

