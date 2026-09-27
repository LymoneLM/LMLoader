# LMLoader.Templates

`dotnet new` 模组项目模板包。

```bash
dotnet tool install -g LMLoader.Cli      # 作者工具链(lint/pack)
dotnet new install LMLoader.Templates    # 安装模板(nuget.org 发布后)
dotnet new lmmodule -n MyMod --modUid com.author.mymod --gameId com.my.game
```

生成:LmMod 基类入口(`[LmMod]` 特性绑定)+ mod.json 清单 + 作者惯例 csproj。
