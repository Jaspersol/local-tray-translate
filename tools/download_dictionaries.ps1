$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$rawDir = Join-Path $PSScriptRoot '..\data\raw'
New-Item -ItemType Directory -Force -Path $rawDir | Out-Null

$ecdictZip = Join-Path $rawDir 'ecdict.zip'
$ecdictCsv = Join-Path $rawDir 'ecdict.csv'
$cedictGz = Join-Path $rawDir 'cedict_ts.u8.gz'
$cedictTxt = Join-Path $rawDir 'cedict_ts.u8'

Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv' -OutFile $ecdictCsv -UseBasicParsing -TimeoutSec 180
Invoke-WebRequest -Uri 'https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.txt.gz' -OutFile $cedictGz -UseBasicParsing -TimeoutSec 180

$inputStream = [System.IO.File]::OpenRead($cedictGz)
try {
    $gzipStream = [System.IO.Compression.GzipStream]::new($inputStream, [System.IO.Compression.CompressionMode]::Decompress)
    try {
        $outputStream = [System.IO.File]::Create($cedictTxt)
        try {
            $gzipStream.CopyTo($outputStream)
        }
        finally {
            $outputStream.Dispose()
        }
    }
    finally {
        $gzipStream.Dispose()
    }
}
finally {
    $inputStream.Dispose()
}

Get-Item $ecdictCsv, $cedictTxt | Select-Object FullName, Length
