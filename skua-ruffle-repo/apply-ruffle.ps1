# Aplica as 3 edicoes do protótipo Ruffle no codigo do Skua (rodado pelo workflow).
$ErrorActionPreference = "Stop"

# 1) DI: usar RuffleFlashUtil
$cs = "Skua.WPF/Services/ConfigureServices.cs"
$t = Get-Content $cs -Raw
if ($t -notmatch "RuffleFlashUtil") {
    $n = $t.Replace("AddSingleton<IFlashUtil, FlashUtil>()", "AddSingleton<IFlashUtil, RuffleFlashUtil>()")
    if ($n -eq $t) { throw "Nao achei AddSingleton<IFlashUtil, FlashUtil>() em $cs" }
    Set-Content $cs $n -NoNewline -Encoding UTF8
}

# 2) WebView2 no Skua.WPF
$p = "Skua.WPF/Skua.WPF.csproj"
$t = Get-Content $p -Raw
if ($t -notmatch "Microsoft.Web.WebView2") {
    $add = "  <ItemGroup>`n    <PackageReference Include=`"Microsoft.Web.WebView2`" Version=`"1.0.*`" />`n  </ItemGroup>`n</Project>"
    Set-Content $p $t.Replace("</Project>", $add) -NoNewline -Encoding UTF8
}

# 3) copiar ruffle-host junto com o app
$p = "Skua.App.WPF/Skua.App.WPF.csproj"
$t = Get-Content $p -Raw
if ($t -notmatch "ruffle-host") {
    $add = "  <ItemGroup>`n    <None Include=`"..\ruffle-host\**`" Link=`"ruffle-host\%(RecursiveDir)%(Filename)%(Extension)`">`n      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>`n    </None>`n  </ItemGroup>`n</Project>"
    Set-Content $p $t.Replace("</Project>", $add) -NoNewline -Encoding UTF8
}

# 4) skua.swf do usuario no lugar esperado pelo build
New-Item -ItemType Directory -Force "Skua.AS3/skua/bin" | Out-Null
Copy-Item "prebuilt/skua.swf" "Skua.AS3/skua/bin/skua.swf" -Force
Write-Host "Patch aplicado."
