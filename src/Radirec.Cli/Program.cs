using Radirec.Cli.Core;

// radirec.exe の入口。コマンド・JSON・終了コード・CommandApp の設定はテストできる Radirec.Cli.Core にあり(#178)、
// ここは版(この exe のアセンブリに CI が刻んだもの)を渡して走らせるだけ。
// JSON と表を UTF-8 で出す(日本語を含む JSON を、エージェントやパイプの先がそのまま読めるように)。
Console.OutputEncoding = System.Text.Encoding.UTF8;
return await RadirecCli.RunAsync(args, VersionInfo.Of(typeof(Program).Assembly));
