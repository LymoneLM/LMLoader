# MyMod(LMLoader 模组)

- 构建:`dotnet build` — 产物(dll + 自带依赖)即模组目录内容。
- 部署:整个目录放进游戏 `mods/` 下(清单 `*mod.json` 会被自动发现)。
- 校验/打包:`dotnet tool install -g LMLoader.Cli` 后 `lmcli lint --dir .`、`lmcli pack-ts ...`。
- pck 资源(可选):独立 Godot 工程导出 pack.pck 放进本目录并在清单 `pck` 里声明;
  模组工程若放进游戏工程目录,需放 `.gdignore`。
- uid 双写一致性:mod.json 与 [LmMod] 特性必须相同,`lmcli lint` 会交叉校验。
