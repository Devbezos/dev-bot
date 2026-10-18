$path = 'D:\Code\dev-ui\src\pages\ExpensesPage.tsx'
$content = Get-Content -Raw $path
$content = $content.Replace(
"function formatPercent(value: number): string {
  return `${value.toFixed(2)}%`
}
",
"function formatPercent(value: number): string {
  return `${value.toFixed(2)}%`
}

function formatWholePercent(value: number): string {
  return `${Math.round(value)}%`
}
"
)
Set-Content -LiteralPath $path -Value $content
