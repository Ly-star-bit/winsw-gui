# WinSW XML 配置速查表

WinSW 3.x 配置文件的单页完整规范。

这份文档是写给 **AI 助手** 看的：把整页复制给 AI，再补上你自己程序的描述，AI 就能一次
生成正确的 `<service>` 文档。下面所有元素名、默认值和取值范围都直接取自 WinSW 的解析器
源码，可以当作权威依据。

---

## 1. 提示词模板

复制本页全文，然后在后面追加下面这段。

```text
你要生成一个 WinSW 3.x 服务配置文件。严格遵循上面的规范。规则：
  - 只输出一个完整的 XML 文档，前后不要有任何解释性文字。
  - 只使用规范中列出的元素名和属性名。它们区分大小写；拼错或大小写不对的元素
    会被静默忽略，不报错。
  - 只写我确实需要的元素，不要输出空元素。
  - 在不太直观的元素上方加一行 XML 注释说明。
  - 最后附一段“自检”，逐条核对第 9 节的检查清单。

我的程序：
  - 是什么：        <例如 一个 Spring Boot jar>
  - 命令行：        <例如 java -Xmx1g -jar app.jar --server.port=8080>
  - 安装目录：      <例如 D:\apps\myapp>
  - 运行账户：      <LocalSystem | 某个域账户 | NetworkService>
  - 启动类型：      <自动 | 自动（延迟）| 手动>
  - 崩溃后：        <例如 10 秒后重启，再崩就 60 秒后重启>
  - 日志：          <例如 按天切分，保留 30 天>
  - 其它要求：      <环境变量、依赖服务、映射网络驱动器、启动前脚本……>
```

---

## 2. 骨架

根元素必须是 `<service>`，子元素顺序随意。

```xml
<?xml version="1.0" encoding="UTF-8"?>
<service>
  <id>myapp</id>
  <name>My Application</name>
  <description>这个服务是做什么的。</description>
  <executable>%BASE%\bin\myapp.exe</executable>
  <arguments>--config %BASE%\conf\app.yml</arguments>
  <log mode="roll-by-size">
    <sizeThreshold>10240</sizeThreshold>
    <keepFiles>8</keepFiles>
  </log>
</service>
```

必填的只有 `<id>` 和 `<executable>`，其余都有默认值。

**文件放哪。** 传统做法是把配置文件和（通常已改名的）WinSW 可执行文件放在同一目录、主文件名
相同：`myapp.exe` → `myapp.xml`。但 3.x 里这不是硬性要求 —— `winsw install <配置>` 会把两个路径
都记进服务，所以一个 wrapper 可以服务于放在别处的多份配置。真正要记住的是：**一切都从配置文件
推导，而不是从 wrapper 推导** —— `%BASE%` 是配置文件所在目录，日志文件名也取自配置文件
（`myapp.xml` → `myapp.out.log`）。

---

## 3. 全局规则

| 规则 | 说明 |
| --- | --- |
| 编码 | `XmlDocument` 能识别的编码都行。推荐 UTF-8 并带上 `<?xml?>` 声明；文件里有中文时务必这么写。 |
| 大小写 | 元素名和属性名用 XPath 匹配，**区分大小写**。`<delayedautostart>` 不起作用，`<delayedAutoStart>` 才对。准确拼写见第 4 节。 |
| 未知元素 | 直接忽略，不报错。所以拼错元素名是静默失效的 —— 这是最常见的坑。 |
| `%VAR%` 展开 | 每个元素的文本、每个 `<env value="…">` 都会做一次 Windows 环境变量展开。未定义的变量原样保留。没有转义写法可以输出字面的 `%`。 |
| 内置变量 | `%BASE%` —— 包装器可执行文件所在目录；`%SERVICE_ID%` 和 `%WINSW_SERVICE_ID%` —— `<id>` 的值；`%WINSW_EXECUTABLE%` —— 包装器可执行文件的完整路径。 |
| 相对路径 | 相对于工作目录解析，而工作目录默认是配置文件所在目录。建议一律写成 `%BASE%\…`。 |
| 布尔值 | 用 .NET `bool.Parse` 解析：只接受 `true` / `false`（大小写随意）。`1`、`0`、`yes`、`on` 会抛异常。 |
| 时长 | 整数 + 可选单位后缀：`ms`、`sec`、`secs`、`min`、`mins`、`hr`、`hrs`、`hour`、`hours`、`day`、`days`。单位前的空格可有可无。不写单位表示**毫秒**。小数（`1.5 min`）不接受，要写 `90 sec`。 |
| XML 转义 | 元素文本里的 `&` `<` `>` 必须转义成 `&amp;` `&lt;` `&gt;`，`<arguments>` 里也一样。 |
| 注释 | 图形控制台在保存时会保留注释，可以放心写。 |

---

## 4. 元素索引

出现次数：`1` 必填一次，`?` 可选一次，`*` 可重复。

### 身份标识 —— 安装期生效

| 元素 | | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | 1 | 字符串 | — | Windows 服务名。整机唯一，建议只用字母数字。改了就必须重新安装。 |
| `name` | ? | 字符串 | *空* | services.msc 里显示的名称，可以带空格和中文。 |
| `description` | ? | 字符串 | *空* | services.msc 里显示的描述。 |
| `startmode` | ? | `Automatic` \| `Manual` \| `Disabled` \| `Boot` \| `System` | `Automatic` | 不区分大小写。`Boot`/`System` 只对驱动服务有意义。 |
| `delayedAutoStart` | ? | 布尔 | `false` | 只有 `startmode` 为 `Automatic` 时才有效。 |
| `depend` | * | 字符串 | — | 必须先启动的服务的 **id**（不是显示名）。一个依赖写一个元素。 |
| `serviceaccount` | ? | 元素 | LocalSystem | 见第 6 节。 |
| `onfailure` | * | 元素 | — | 见第 7 节。 |
| `resetfailure` | ? | 时长 | `1 day` | 服务连续运行多久之后，SCM 把失败计数清零。 |
| `securityDescriptor` | ? | SDDL 字符串 | — | 服务的安全描述符。 |
| `preshutdown` | ? | 布尔 | `false` | 注册预关机通知，让服务在系统关机时能多拿到一段时间。 |
| `preshutdownTimeout` | ? | 时长 | 系统默认（3 分钟） | 只配合 `preshutdown` 使用。 |

> 这一组是 `install` / `refresh` 时写进 SCM 的。改了之后光重启服务没有任何效果，必须再执行
> `winsw refresh myapp.xml`（或在图形控制台点“保存并应用”）。

### 进程

| 元素 | | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| `executable` | 1 | 路径 | — | 绝对路径，或者依赖 `PATH` 查找的名字。服务的 `PATH` 常常和你的命令行不一样，建议写绝对路径。 |
| `arguments` | ? | 字符串 | *空* | 原样传递。元素内的换行和缩进会被保留，所以可以写成多行，但那些空白也会进命令行。 |
| `startarguments` | ? | 字符串 | — | 启动时覆盖 `arguments`。用了 `stoparguments` 就**必须**用它。 |
| `workingdirectory` | ? | 路径 | 配置文件所在目录 | |
| `priority` | ? | `Idle` \| `BelowNormal` \| `Normal` \| `AboveNormal` \| `High` \| `RealTime` | `Normal` | 不区分大小写。调到 `Normal` 以上通常弊大于利。 |
| `hidewindow` | ? | 布尔 | `false` | 以 `CreateNoWindow` 启动子进程，不弹控制台窗口。 |
| `interactive` | ? | 布尔 | `false` | 无效。它被解析后就没人读，而它当年依赖的机制已在 2018 年从 Windows 中移除。需要桌面的程序只能做成[桌面任务](desktop-tasks.zh-CN.md)，不能做成服务。 |
| `beeponshutdown` | ? | 布尔 | `false` | 调试用，关机时蜂鸣。 |
| `env` | * | 属性 | — | `<env name="KEY" value="VALUE" />`，两个属性都必填，值会做 `%VAR%` 展开。 |
| `proxy` | ? | 文本 + 属性 | — | `<proxy noProxy="localhost,.corp" java="true">http://proxy.example.com:8080</proxy>`。给子进程设置 `HTTP_PROXY`、`HTTPS_PROXY`、`NO_PROXY`。JVM 不认这几个变量，所以 `java="true"` 会再把 `-Dhttp.proxyHost` 等选项放到 `JAVA_TOOL_OPTIONS` 最前面。协议头必须写。同名的 `env` 优先。 |
| `download` | * | 属性 | — | 见第 8 节。 |
| `sharedDirectoryMapping` | ? | 元素 | — | 启动前把 UNC 路径映射成盘符：`<map label="N:" uncpath="\\server\share" />`，可重复，两个属性都必填。 |
| `autoRefresh` | ? | 布尔 | `true` | 服务每次启动/停止/重启时，自动把 XML 里的安装期设置重新应用一遍。 |

### 停止

| 元素 | | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| `stoptimeout` | ? | 时长 | `15 sec` | 发出 Ctrl+C / WM_CLOSE 之后，强杀前给进程的宽限时间。 |
| `stopexecutable` | ? | 路径 | `executable` 的值 | 只有存在 `stoparguments` 时才使用。 |
| `stoparguments` | ? | 字符串 | — | 把停止方式从“给进程发信号”换成“执行一条停止命令并等待”。此时要用 `startarguments` 而不是 `arguments`。 |

### 生命周期钩子

`prestart`、`poststart`、`prestop`、`poststop` —— 都是可选的，结构完全相同：

```xml
<prestart>
  <executable>%BASE%\hooks\prepare.cmd</executable>
  <arguments>--verbose</arguments>
  <stdoutPath>%BASE%\logs\prestart.out.log</stdoutPath>
  <stderrPath>NUL</stderrPath>
</prestart>
```

| 钩子 | 执行时机 |
| --- | --- |
| `prestart` | 启动过程中，主进程启动之前 |
| `poststart` | 启动过程中，主进程启动之后 |
| `prestop` | 停止过程中，主进程停止之前 |
| `poststop` | 停止过程中，主进程已经停止之后 |

`stdoutPath` / `stderrPath` 重定向钩子自己的输出，写 `NUL` 表示丢弃。注意驼峰拼写 ——
`<stdoutpath>` 会被忽略。

### 日志

下面这些是**顶层**元素，不要写进 `<log>` 里面：

| 元素 | | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| `logpath` | ? | 路径 | 配置文件所在目录 | 日志文件目录。 |
| `logname` | ? | 字符串 | 配置文件主名 | 文件名前缀，例如 `myapp` → `myapp.out.log`。 |
| `outfiledisabled` | ? | 布尔 | `false` | 完全丢弃标准输出。 |
| `errfiledisabled` | ? | 布尔 | `false` | 完全丢弃标准错误。 |
| `outfilepattern` | ? | 字符串 | `.out.log` | 追加在 `logname` 后面的后缀。**只在 `roll-by-size`、`roll-by-time`、`roll-by-size-time`、`rotate` 模式下生效** —— `append`、`reset`、`roll` 三个 appender 把 `.out.log` / `.err.log` 写死了。 |
| `errfilepattern` | ? | 字符串 | `.err.log` | 同上限制。 |

模式本身写在 `<log mode="…">` 上（旧写法 `<logmode>` 仍然可用）：

| 模式 | 行为 | 内嵌元素 |
| --- | --- | --- |
| `append` *(默认)* | 一直追加到 `<logname>.out.log` / `.err.log`，文件会无限增长。 | — |
| `reset` | 每次启动清空两个文件。 | — |
| `none` | 丢弃输出，不产生日志文件。 | — |
| `roll` | 类似 `append`，但启动时把上一份改名为 `*.old.log`。 | — |
| `roll-by-size` | 超过指定大小就滚动为 `myapp.1.out.log`、`myapp.2.out.log`…… | `sizeThreshold`（KB，默认 `10240`）、`keepFiles`（默认 `8`） |
| `roll-by-time` | 按时间周期切分，文件名用时间戳格式。 | `pattern` **（必填）**、`period`（天，默认 `1`）、`keepFiles`（默认全部保留） |
| `roll-by-size-time` | 按大小滚动 + 时间戳命名，还可以在固定时刻滚动并压缩旧文件。 | `sizeThreshold`（KB，默认 `10240`）、`pattern` **（必填）**、`autoRollAtTime`（`HH:mm:ss`）、`zipOlderThanNumDays`（整数）、`zipDateFormat`（默认 `yyyyMM`） |
| `rotate` | `roll-by-size` 的废弃别名，直接用 `roll-by-size`。 | — |

`pattern` 用 .NET `DateTime.ToString` 的格式串，例如 `yyyyMMdd`。
`zipOlderThanNumDays` 和 `zipDateFormat` 必须和 `autoRollAtTime` 一起用才有效。

```xml
<logpath>%BASE%\logs</logpath>
<logname>myapp</logname>
<log mode="roll-by-time">
  <pattern>yyyyMMdd</pattern>
  <keepFiles>30</keepFiles>
</log>
```

### 扩展

```xml
<extensions>
  <extension enabled="true" id="mapNetworkDrives" className="…">
    <!-- 扩展自己的配置 -->
  </extension>
</extensions>
```

WinSW 3.x 没有内置扩展，共享目录映射已经变成顶层的 `<sharedDirectoryMapping>` 元素。
除非你用的是自己合并了扩展 DLL 的定制版本，否则不要写 `<extensions>`。

---

## 5. 时长和布尔值写法

```xml
<stoptimeout>30 sec</stoptimeout>   <!-- 可以 -->
<stoptimeout>30sec</stoptimeout>    <!-- 可以，空格可省 -->
<stoptimeout>1 min</stoptimeout>    <!-- 可以 -->
<stoptimeout>30000</stoptimeout>    <!-- 可以：纯数字 = 毫秒 -->
<stoptimeout>0.5 min</stoptimeout>  <!-- 错误：不是整数 -->
<stoptimeout>30 s</stoptimeout>     <!-- 错误：没有 s 这个单位 -->
<stoptimeout>30 SEC</stoptimeout>   <!-- 错误：单位必须小写 -->

<hidewindow>true</hidewindow>       <!-- 可以 -->
<hidewindow>1</hidewindow>          <!-- 错误 -->
```

---

## 6. 服务账户

不写这个元素时默认用 LocalSystem。

```xml
<serviceaccount>
  <username>DOMAIN\svc_myapp</username>
  <password>Pa55w0rd</password>
  <allowservicelogon>true</allowservicelogon>
</serviceaccount>
```

| 子元素 | 说明 |
| --- | --- |
| `username` | `DOMAIN\User`、`User@DOMAIN`，本机账户用 `.\User`。 |
| `password` | 明文存在 XML 里 —— 一定要用 NTFS 权限保护好这个文件。 |
| `allowservicelogon` | `true` 表示安装时自动给该账户授予“作为服务登录”权限。 |
| `prompt` | `dialog` 或 `console` —— 安装时弹窗/在控制台询问凭据，而不是把密码写进文件。 |

内置账户不需要密码：

```xml
<serviceaccount><username>LocalSystem</username></serviceaccount>
<serviceaccount><username>NT AUTHORITY\LocalService</username></serviceaccount>
<serviceaccount><username>NT AUTHORITY\NetworkService</username></serviceaccount>
```

组托管服务账户（gMSA）：账户名后面加 `$`，并且不要写 `<password>`。

```xml
<serviceaccount>
  <username>DOMAIN\gmsa_myapp$</username>
  <allowservicelogon>true</allowservicelogon>
</serviceaccount>
```

---

## 7. 失败动作

```xml
<onfailure action="restart" delay="10 sec" />
<onfailure action="restart" delay="60 sec" />
<onfailure action="none" />
<resetfailure>1 hour</resetfailure>
```

- `action` 必填：`restart`、`reboot` 或 `none`。
- `delay` 可选，默认 `0`，格式见第 3 节的时长规则。
- 元素按顺序消费：第一次失败用第一个，第二次用第二个，以此类推。
- 列表用完之后，**最后一个**动作会对之后每次失败一直重复。所以只写一个
  `<onfailure action="restart" delay="10 sec" />` 就等于“永远自动重启”。
- `reboot` 会让 Windows 蓝屏重启，慎用。
- `resetfailure` 是服务要连续正常运行多久，失败计数才归零。

注意：这些动作是在 *服务* 被判定为失败时触发的，对 WinSW 来说就是被包装的进程以非 0
退出码结束。

---

## 8. 下载

在主程序启动之前执行，每次启动都会跑一遍。

```xml
<download from="https://example.com/some.dat" to="%BASE%\some.dat" />
<download from="https://example.com/some.dat" to="%BASE%\some.dat" failOnError="true" />
<download from="https://example.com/some.dat" to="%BASE%\some.dat" auth="sspi" />
<download from="https://example.com/some.dat" to="%BASE%\some.dat"
          auth="basic" user="aUser" password="aPassw0rd" />
<download from="http://example.com/some.dat" to="%BASE%\some.dat"
          auth="basic" unsecureAuth="true" user="aUser" password="aPassw0rd" />
<download from="https://example.com/some.dat" to="%BASE%\some.dat"
          proxy="http://user:pass@192.168.1.5:8080/" />
```

| 属性 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `from` | 是 | — | 源 URL。 |
| `to` | 是 | — | 目标文件路径。 |
| `failOnError` | 否 | `false` | `true` 时下载失败会让服务启动失败。 |
| `auth` | 否 | `none` | `none`、`sspi`（Kerberos/NTLM）或 `basic`。 |
| `user` | 用 `basic` 时必填 | — | **属性名是 `user`，不是 `username`。** 仓库里旧的 `complete.xml` 示例在这一点上是错的。 |
| `password` | 用 `basic` 时必填 | — | |
| `unsecureAuth` | 否 | `false` | 想在明文 HTTP 上用 `basic` 就必须打开，否则包装器直接拒绝。 |
| `proxy` | 否 | — | `http://HOST:PORT/` 或 `http://USER:PASS@HOST:PORT/`。它只管这一次下载；被包装的程序走哪个代理由顶层的 `<proxy>` 元素决定。 |

如果目标文件已存在，包装器会带上 `If-Modified-Since` 请求头，服务端返回 `304 Not Modified`
时跳过下载。

---

## 9. 交付前检查清单

1. 根元素是 `<service>`，整个文档是合法 XML。
2. `<id>` 在这台机器上唯一，且不含空格。
3. `<executable>` 是绝对路径（或基于 `%BASE%`），并且文件确实存在。
4. 凡是位于安装目录里的路径，都写成 `%BASE%\…`。
5. `<arguments>` 里的 `&`、`<`、`>` 已经转义。
6. 元素名和第 4 节**完全一致**，包括驼峰拼写
   （`delayedAutoStart`、`preshutdownTimeout`、`securityDescriptor`、`autoRefresh`、
   `sharedDirectoryMapping`、`stdoutPath`、`stderrPath`、`sizeThreshold`、`keepFiles`、
   `autoRollAtTime`、`zipOlderThanNumDays`、`zipDateFormat`）。
7. 布尔值是 `true`/`false`；时长是整数 + 小写单位。
8. 写了 `<stoparguments>` 就要用 `<startarguments>` 而不是 `<arguments>`。
9. 日志模式是 `roll-by-time` 或 `roll-by-size-time` 时，`<pattern>` 必须存在。
10. `<outfilepattern>` / `<errfilepattern>` 只在支持它们的模式下才写。
11. `<depend>` 里写的是服务 id，不是显示名。
12. 含明文密码的 `<serviceaccount>` 只用在已经限制了 NTFS 权限的文件上。
13. 改完安装期设置后执行 `winsw refresh`，光重启服务是不生效的。

---

## 10. 完整示例

### 最小配置

```xml
<service>
  <id>myapp</id>
  <executable>%BASE%\myapp.exe</executable>
</service>
```

### Java 应用：按天切日志，崩溃自动重启

```xml
<?xml version="1.0" encoding="UTF-8"?>
<service>
  <id>jenkins</id>
  <name>Jenkins</name>
  <description>Jenkins 持续集成服务器。</description>

  <env name="JENKINS_HOME" value="%BASE%" />
  <executable>java</executable>
  <arguments>-Xrs -Xmx1g -jar "%BASE%\jenkins.war" --httpPort=8080</arguments>

  <startmode>Automatic</startmode>
  <delayedAutoStart>true</delayedAutoStart>
  <stoptimeout>1 min</stoptimeout>

  <onfailure action="restart" delay="10 sec" />
  <onfailure action="restart" delay="60 sec" />
  <resetfailure>1 hour</resetfailure>

  <logpath>%BASE%\logs</logpath>
  <log mode="roll-by-time">
    <pattern>yyyyMMdd</pattern>
    <keepFiles>30</keepFiles>
  </log>
</service>
```

### 指定账户 + 依赖服务 + 启动前钩子 + 按大小切日志

```xml
<?xml version="1.0" encoding="UTF-8"?>
<service>
  <id>svc-report</id>
  <name>报表生成服务</name>
  <description>每晚从数据仓库生成报表。</description>

  <serviceaccount>
    <username>CORP\svc_report</username>
    <password>Pa55w0rd</password>
    <allowservicelogon>true</allowservicelogon>
  </serviceaccount>

  <depend>MSSQLSERVER</depend>

  <workingdirectory>%BASE%</workingdirectory>
  <executable>%BASE%\python\python.exe</executable>
  <arguments>-u service.py --config %BASE%\conf\prod.ini</arguments>
  <hidewindow>true</hidewindow>
  <priority>BelowNormal</priority>

  <prestart>
    <executable>%BASE%\hooks\wait-for-db.cmd</executable>
    <stdoutPath>%BASE%\logs\prestart.log</stdoutPath>
    <stderrPath>%BASE%\logs\prestart.log</stderrPath>
  </prestart>

  <sharedDirectoryMapping>
    <map label="R:" uncpath="\\fileserver\reports" />
  </sharedDirectoryMapping>

  <logpath>%BASE%\logs</logpath>
  <logname>report</logname>
  <log mode="roll-by-size">
    <sizeThreshold>20480</sizeThreshold>
    <keepFiles>10</keepFiles>
  </log>

  <onfailure action="restart" delay="30 sec" />
  <resetfailure>2 hours</resetfailure>
</service>
```

### 用停止命令优雅停机

```xml
<service>
  <id>tomcat</id>
  <name>Apache Tomcat</name>
  <executable>%BASE%\bin\catalina.bat</executable>
  <startarguments>run</startarguments>
  <stopexecutable>%BASE%\bin\catalina.bat</stopexecutable>
  <stoparguments>stop</stoparguments>
  <stoptimeout>2 min</stoptimeout>
</service>
```

---

## 11. 相关文档

- [XML 配置文件](xml-config-file.md) —— 完整的叙述式参考（英文）。
- [日志与错误报告](logging-and-error-reporting.md)
- [命令行命令](cli-commands.md)
- [图形控制台](gui.md)
