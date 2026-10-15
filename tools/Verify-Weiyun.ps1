<#
Native .NET connection proof for the Weiyun implementation gate.
Tokens stay DPAPI protected in artifacts; only synthetic files are uploaded.
An OAuth URL is printed for the operator to open and authorize personally.
#>
[CmdletBinding()]
param(
    [switch]$ReuseSession,
    [switch]$SecondAccount
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$probeDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/weiyun-probe'
[IO.Directory]::CreateDirectory($probeDirectory) | Out-Null
$credentialPath = Join-Path $probeDirectory $(if ($SecondAccount) { 'credentials-second.dpapi' } else { 'credentials.dpapi' })
$reportPath = Join-Path $probeDirectory $(if ($SecondAccount) { 'result-second.json' } else { 'result.json' })
$mcpUri = 'https://www.weiyun.com/api/v3/mcpserver'
$script:requestId = 0
$script:session = $null
$report = [ordered]@{
    completed = $false; passed = $false; implementationGatePassed = $false; stage = 'authorization'; utc = [DateTime]::UtcNow.ToString('o')
    authorization = $false; upload = $false; download = $false; refresh = $false
    rootStableAfterRefresh = $false; accountIsolation = 'not_verified'
    publicDistribution = 'not_verified'; probeDirectory = $null; failure = $null
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
function Save-Session($Session, [string]$Path) {
    $plain = [Text.Encoding]::UTF8.GetBytes(($Session | ConvertTo-Json -Depth 15 -Compress))
    try {
        $protected = [Security.Cryptography.ProtectedData]::Protect($plain, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes($Path + '.tmp', $protected)
        [IO.File]::Move($Path + '.tmp', $Path, $true)
    }
    finally { [Array]::Clear($plain, 0, $plain.Length) }
}
function Read-Session([string]$Path) {
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Path), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { return [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json }
    finally { [Array]::Clear($plain, 0, $plain.Length) }
}
function Invoke-Tool([string]$Name, $Arguments) {
    $script:requestId++
    $body = @{ jsonrpc = '2.0'; id = $script:requestId; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } } | ConvertTo-Json -Depth 25 -Compress
    $envelope = Invoke-RestMethod -Method Post -Uri $mcpUri -ContentType 'application/json' -Headers @{
        Authorization = 'Bearer ' + $script:session.tokens.access_token
        Accept = 'application/json, text/event-stream'
    } -Body $body
    if ($envelope.error -or $envelope.result.isError) { throw "MCP request failed: $Name" }
    $result = $envelope.result.structuredContent
    if (-not $result) {
        $textBlock = $envelope.result.content | Where-Object type -eq 'text' | Select-Object -First 1
        if (-not $textBlock) { throw "MCP result missing: $Name" }
        $result = $textBlock.text | ConvertFrom-Json
    }
    if ($result.error) { throw "Weiyun request failed: $Name" }
    return $result
}
function Get-Directory([string]$Key = '', [string]$Parent = '') {
    $directories = @(); $files = @(); $offset = 0; $currentKey = $null
    do {
        $arguments = @{ limit = 50; offset = $offset; get_type = 0; order_by = 1; asc = $true }
        if ($Key) { $arguments.dir_key = $Key }
        if ($Parent) { $arguments.pdir_key = $Parent }
        $page = Invoke-Tool 'weiyun.list' $arguments
        if (-not $page.pdir_key) { throw 'Directory identity is missing' }
        if ($currentKey -and $currentKey -cne $page.pdir_key) { throw 'Directory identity changed during pagination' }
        $currentKey = $page.pdir_key
        $directories += @($page.dir_list); $files += @($page.file_list)
        $received = @($page.dir_list).Count + @($page.file_list).Count
        if (-not $page.finish_flag -and $received -eq 0) { throw 'Directory pagination made no progress' }
        $offset += $received
        if ($offset -gt 100000) { throw 'Directory pagination limit exceeded' }
    } while (-not $page.finish_flag)
    return @{ key = $currentKey; directories = $directories; files = $files }
}
function New-Login {
    $metadata = Invoke-RestMethod -Uri 'https://www.weiyun.com/.well-known/oauth-authorization-server'
    # Never send a code or refresh token to an endpoint supplied by a different issuer.
    if ($metadata.issuer -cne 'https://www.weiyun.com' -or
        $metadata.authorization_endpoint -cne 'https://www.weiyun.com/authorize/oauth' -or
        $metadata.registration_endpoint -cne 'https://www.weiyun.com/api/v3/mcpserver/oauth/register' -or
        $metadata.token_endpoint -cne 'https://www.weiyun.com/api/v3/mcpserver/oauth/token' -or
        'S256' -notin $metadata.code_challenge_methods_supported -or
        'none' -notin $metadata.token_endpoint_auth_methods_supported) { throw 'Unsupported OAuth metadata' }
    $listener = [Net.HttpListener]::new()
    try {
        # Binding first avoids a port-selection/callback-listener race.
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $port = [Security.Cryptography.RandomNumberGenerator]::GetInt32(42000, 60000)
            $listener.Prefixes.Clear()
            $listener.Prefixes.Add("http://127.0.0.1:$port/")
            try { $listener.Start(); break } catch [Net.HttpListenerException] { if ($attempt -eq 19) { throw } }
        }
        $redirect = "http://127.0.0.1:$port/callback"
        $registration = @{ client_name = 'ToDoList desktop connection verification'; redirect_uris = @($redirect)
            grant_types = @('authorization_code', 'refresh_token'); response_types = @('code')
            token_endpoint_auth_method = 'none'; scope = 'files:read files:write files:manage' } | ConvertTo-Json -Compress
        $client = Invoke-RestMethod -Method Post -Uri $metadata.registration_endpoint -ContentType 'application/json' -Body $registration
        if (-not $client.client_id -or $client.token_endpoint_auth_method -ne 'none') { throw 'Public client registration failed' }
        $verifier = ConvertTo-Base64Url ([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $challenge = ConvertTo-Base64Url ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($verifier)))
        $state = ConvertTo-Base64Url ([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $parameters = [ordered]@{ client_id = $client.client_id; redirect_uri = $redirect; response_type = 'code'
            scope = 'files:read files:write files:manage'; state = $state; code_challenge = $challenge
            code_challenge_method = 'S256'; resource = $mcpUri }
        $query = ($parameters.GetEnumerator() | ForEach-Object { [Uri]::EscapeDataString($_.Key) + '=' + [Uri]::EscapeDataString([string]$_.Value) }) -join '&'
        $authUri = $metadata.authorization_endpoint + '?' + $query
        Write-Output ('AUTH_URL=' + $authUri)
        Write-Output 'WAITING_FOR_PERSONAL_WECHAT_AUTHORIZATION (5 minutes)'
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $code = $null
        while ($clock.Elapsed.TotalMinutes -lt 5) {
            $pending = $listener.GetContextAsync()
            $remaining = [Math]::Max(1, 300000 - [int]$clock.Elapsed.TotalMilliseconds)
            if (-not $pending.Wait($remaining)) { throw 'Authorization timed out' }
            $context = $pending.Result
            $valid = $context.Request.HttpMethod -eq 'GET' -and $context.Request.Url.AbsolutePath -eq '/callback' -and
                $context.Request.QueryString['state'] -ceq $state
            $responseText = if ($valid) { 'ToDoList authorization received. Return to Codex.' } else { 'Invalid callback.' }
            $responseBytes = [Text.Encoding]::UTF8.GetBytes($responseText)
            $context.Response.StatusCode = if ($valid) { 200 } else { 400 }
            $context.Response.ContentType = 'text/plain; charset=utf-8'
            $context.Response.OutputStream.Write($responseBytes, 0, $responseBytes.Length)
            $context.Response.Close()
            if (-not $valid) { continue }
            if ($context.Request.QueryString['error']) { throw 'Authorization declined' }
            $code = $context.Request.QueryString['code']
            if ($code) { break }
        }
        if (-not $code) { throw 'Authorization code missing' }
        $tokens = Invoke-RestMethod -Method Post -Uri $metadata.token_endpoint -ContentType 'application/x-www-form-urlencoded' -Body @{
            grant_type = 'authorization_code'; client_id = $client.client_id; code = $code
            redirect_uri = $redirect; code_verifier = $verifier; resource = $mcpUri
        }
        if (-not $tokens.access_token -or -not $tokens.refresh_token) { throw 'OAuth token exchange failed' }
        $script:session = @{ client_id = $client.client_id; tokens = $tokens }
        Save-Session $script:session $credentialPath
    }
    finally { $listener.Close() }
}

try {
    if ($ReuseSession) {
        if (-not [IO.File]::Exists($credentialPath)) { throw 'No protected probe session to reuse' }
        $script:session = Read-Session $credentialPath
    }
    else { New-Login }
    $report.stage = 'directory_identity'
    $root = Get-Directory
    $report.authorization = $true
    $identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('https://www.weiyun.com|' + $root.key))).ToLowerInvariant()
    $report['spaceFingerprint'] = $identity
    if ($SecondAccount) {
        $firstReport = Get-Content -LiteralPath (Join-Path $probeDirectory 'result.json') -Raw | ConvertFrom-Json
        if (-not $firstReport.spaceFingerprint) { throw 'Run the first-account verification first' }
        if ($firstReport.spaceFingerprint -ceq $identity) { throw 'Second authorization used the same cloud space' }
        $report.accountIsolation = 'different_space_verified'
    }
    $report.stage = 'application_directory'
    $matches = @($root.directories | Where-Object dir_name -ceq 'ToDoList')
    if ($matches.Count -gt 1) { throw 'Multiple ToDoList directories require operator review' }
    if ($matches.Count -eq 1) { $appKey = $matches[0].dir_key }
    else {
        $appDir = Invoke-Tool 'weiyun.create_dir' @{ dir_name = 'ToDoList'; pdir_key = $root.key }
        if ($appDir.dir_name -cne 'ToDoList') { throw 'ToDoList directory was renamed by the server' }
        $appKey = $appDir.dir_key
    }
    if (-not $appKey) { throw 'Application directory identity missing' }
    $checkName = 'ConnectionCheck-' + [Guid]::NewGuid().ToString('N')
    $checkDir = Invoke-Tool 'weiyun.create_dir' @{ dir_name = $checkName; pdir_key = $appKey }
    if ($checkDir.dir_name -cne $checkName -or -not $checkDir.dir_key) { throw 'Probe directory creation failed' }
    $report.probeDirectory = 'ToDoList/' + $checkName
    $payload = [Text.Encoding]::UTF8.GetBytes(('{"probe":"ToDoList","nonce":"' + [Guid]::NewGuid().ToString('N') + '"}'))
    # A payload below 128 bytes uses the initial SHA1 internal state for check_sha.
    if ($payload.Length -ge 128) { throw 'Probe payload exceeds the small-file hash fixture' }
    $sha = [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($payload)).ToLowerInvariant()
    $hashState = '0123456789abcdeffedcba9876543210f0e1d2c3'
    $pre = @{ filename = 'connection-check.json'; file_size = $payload.Length; file_sha = $sha
        file_md5 = [Convert]::ToHexString([Security.Cryptography.MD5]::HashData($payload)).ToLowerInvariant()
        block_sha_list = @($sha); check_sha = $hashState; check_data = [Convert]::ToBase64String($payload); pdir_key = $checkDir.dir_key }
    $report.stage = 'upload'
    $uploaded = $false
    for ($round = 0; $round -lt 16; $round++) {
        $upload = Invoke-Tool 'weiyun.upload' $pre
        if ($upload.file_exist -or $upload.upload_state -eq 2) { $uploaded = $true; break }
        $channel = $upload.channel_list | Where-Object len -gt 0 | Select-Object -First 1
        if (-not $channel -or $channel.offset -lt 0 -or $channel.offset -ge $payload.Length) { throw 'Upload channel invalid' }
        $length = [Math]::Min([int]$channel.len, $payload.Length - [int]$channel.offset)
        if ($length -le 0) { throw 'Upload channel length invalid' }
        $upload = Invoke-Tool 'weiyun.upload' @{ filename = $pre.filename; file_size = $payload.Length; file_sha = $sha
            block_sha_list = @(); check_sha = $hashState; upload_key = $upload.upload_key
            channel_list = @($upload.channel_list); channel_id = $channel.id; ex = $upload.ex
            file_data = [Convert]::ToBase64String($payload, [int]$channel.offset, $length); pdir_key = $checkDir.dir_key }
        if ($upload.upload_state -eq 2) { $uploaded = $true; break }
    }
    if (-not $uploaded) { throw 'Upload did not finish' }
    # Weiyun can complete an upload with an empty file_id. Resolve it by listing.
    $listing = Get-Directory $checkDir.dir_key $appKey
    $file = @($listing.files | Where-Object filename -ceq $pre.filename)
    if ($file.Count -ne 1 -or -not $file[0].file_id) { throw 'Completed upload was not found uniquely' }
    $report.upload = $true
    $report.stage = 'download'
    $download = Invoke-Tool 'weiyun.download' @{ items = @(@{ file_id = $file[0].file_id; pdir_key = $listing.key }) }
    if (@($download.items).Count -ne 1) { throw 'Download result missing' }
    $downloadUri = [Uri]$download.items[0].https_download_url
    if ($downloadUri.Scheme -cne 'https') { throw 'Download link is not HTTPS' }
    # Use a separate HTTP client; never forward the OAuth bearer token to the download host.
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $http = [Net.Http.HttpClient]::new($handler)
    try {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $downloadUri)
        if ($download.items[0].cookie) { $request.Headers.TryAddWithoutValidation('Cookie', [string]$download.items[0].cookie) | Out-Null }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode() | Out-Null
        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        if ($bytes.Length -ne $payload.Length -or
            [Convert]::ToBase64String($bytes) -cne [Convert]::ToBase64String($payload)) { throw 'Downloaded bytes do not match the uploaded probe' }
        $report.download = $true
    }
    finally { if ($response) { $response.Dispose() }; if ($request) { $request.Dispose() }; $http.Dispose() }
    $report.stage = 'refresh'
    $newTokens = Invoke-RestMethod -Method Post -Uri 'https://www.weiyun.com/api/v3/mcpserver/oauth/token' -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type = 'refresh_token'; client_id = $script:session.client_id; refresh_token = $script:session.tokens.refresh_token
    }
    if (-not $newTokens.access_token -or -not $newTokens.refresh_token) { throw 'Refresh token response incomplete' }
    $script:session.tokens = $newTokens
    Save-Session $script:session $credentialPath
    $report.refresh = $true
    $newRoot = Get-Directory
    if ($newRoot.key -cne $root.key) { throw 'Authorized space changed after token refresh' }
    $report.rootStableAfterRefresh = $true
    $report.completed = $true
    $report.passed = $true
    $report.stage = 'transport_complete'
}
catch {
    $report.completed = $true
    # Do not print HTTP error bodies, URLs containing authorization codes, or credentials.
    $report.failure = $_.Exception.GetType().FullName
    Write-Output ('VERIFICATION_FAILED=' + $report.failure)
}
finally {
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output ('REPORT=' + $reportPath)
    Write-Output ('TRANSPORT_PASSED=' + $report.passed)
    Write-Output ('ACCOUNT_ISOLATION=' + $report.accountIsolation)
    Write-Output 'PUBLIC_DISTRIBUTION=not_verified'
}
if (-not $report.passed) { exit 1 }
