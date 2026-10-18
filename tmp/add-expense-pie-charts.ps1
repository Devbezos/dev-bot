$path = 'D:\Code\dev-ui\src\pages\ExpensesPage.tsx'
$content = Get-Content -Raw $path

$content = $content.Replace(
"type CappedAllocation = {
  expectedByPersonId: Record<string, number>
  cappedWeeklyByPersonId: Record<string, number | null>
  uncappedMonthlyByPersonId: Record<string, number>
  uncappedWeeklyByPersonId: Record<string, number>
  uncoveredMonthly: number
}",
"type CappedAllocation = {
  expectedByPersonId: Record<string, number>
  cappedWeeklyByPersonId: Record<string, number | null>
  uncappedMonthlyByPersonId: Record<string, number>
  uncappedWeeklyByPersonId: Record<string, number>
  uncoveredMonthly: number
}

type PieSegment = {
  label: string
  amount: number
  color: string
}")

$content = $content.Replace(
"const STORAGE_KEY = 'dev-ui-expenses-sheet-v1'
const NEW_GROUP_VALUE = '__new_group__'
const WEEKS_PER_YEAR = 52
const MONTHS_PER_YEAR = 12
const DAYS_PER_YEAR = 365.2425",
"const STORAGE_KEY = 'dev-ui-expenses-sheet-v1'
const NEW_GROUP_VALUE = '__new_group__'
const WEEKS_PER_YEAR = 52
const MONTHS_PER_YEAR = 12
const DAYS_PER_YEAR = 365.2425
const PERSON_COLORS = ['#7dd3fc', '#fca5a5', '#86efac', '#fcd34d', '#c4b5fd', '#fdba74']
const UNCOVERED_COLOR = '#6b7280'")

$content = $content.Replace(
"function personDisplayName(person: Person, index: number): string {
  return person.name.trim() || `Person ${index + 1}`
}",
"function personDisplayName(person: Person, index: number): string {
  return person.name.trim() || `Person ${index + 1}`
}

function PieChart({ segments }: { segments: PieSegment[] }) {
  const size = 164
  const strokeWidth = 28
  const radius = (size - strokeWidth) / 2
  const circumference = 2 * Math.PI * radius
  const total = segments.reduce((sum, segment) => sum + segment.amount, 0)

  let offset = 0
  const arcs = segments.map(segment => {
    const length = total > 0 ? (segment.amount / total) * circumference : 0
    const arc = (
      <circle
        key={segment.label}
        cx={size / 2}
        cy={size / 2}
        r={radius}
        fill=""transparent""
        stroke={segment.color}
        strokeWidth={strokeWidth}
        strokeDasharray={`${length} ${circumference - length}`}
        strokeDashoffset={-offset}
        strokeLinecap=""butt""
        transform={`rotate(-90 ${size / 2} ${size / 2})`}
      />
    )
    offset += length
    return arc
  })

  return (
    <div className=""relative h-44 w-44 shrink-0"">
      <svg viewBox={`0 0 ${size} ${size}`} className=""h-44 w-44"">
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill=""transparent""
          stroke=""hsl(var(--muted))""
          strokeWidth={strokeWidth}
        />
        {arcs}
      </svg>
      <div className=""pointer-events-none absolute inset-0 flex flex-col items-center justify-center text-center"">
        <div className=""text-xs uppercase tracking-wide text-muted-foreground"">Monthly</div>
        <div className=""text-sm font-semibold text-foreground"">{formatMoney(total)}</div>
      </div>
    </div>
  )
}")

$content = $content.Replace(
"  const incomeShares = useMemo(() => getIncomeShares(people), [people])
",
"  const incomeShares = useMemo(() => getIncomeShares(people), [people])
  const personColorById = useMemo(
    () => Object.fromEntries(people.map((person, index) => [person.id, PERSON_COLORS[index % PERSON_COLORS.Length]])),
    [people]
  )
")

$content = $content.Replace(
"  const totals = useMemo(() => {
",
"  const totals = useMemo(() => {
")

$content = $content.Replace(
"  }, [expenseResults, expenses, people])
",
"  }, [expenseResults, expenses, people])

  const categoryCharts = useMemo(() => {
    $categories = [ordered]@{}
    foreach ($entry in (0..($expenses.Length - 1))) {
    }
    const categories = new Map<string, { totalMonthly: number; byPersonId: Record<string, number> }>()

    expenses.forEach((expense, index) => {
      const category = expense.group.trim() || 'Ungrouped'
      const result = expenseResults[index]
      const current = categories.get(category) ?? {
        totalMonthly: 0,
        byPersonId: Object.fromEntries(people.map(person => [person.id, 0])) as Record<string, number>,
      }

      current.totalMonthly += result.monthlyAmount
      people.forEach(person => {
        current.byPersonId[person.id] += result.sharesByPersonId[person.id] ?? 0
      })

      categories.set(category, current)
    })

    const coveredRatio = totals.totalMonthlyExpenses > 0
      ? Math.max(0, (totals.totalMonthlyExpenses - totals.uncoveredMonthly) / totals.totalMonthlyExpenses)
      : 1

    return Array.from(categories.entries())
      .map(([category, data]) => {
        const coveredTotal = data.totalMonthly * coveredRatio
        const uncoveredTotal = Math.max(data.totalMonthly - coveredTotal, 0)
        const weighted = people.map(person => {
          const uncappedOverall = totals.uncappedMonthlyByPersonId[person.id] ?? 0
          const factor = uncappedOverall > 0 ? (totals.expectedByPersonId[person.id] ?? 0) / uncappedOverall : 0
          return {
            label: personDisplayName(person, people.findIndex(candidate => candidate.id === person.id)),
            amount: (data.byPersonId[person.id] ?? 0) * factor,
            color: String(personColorById[person.id] ?? PERSON_COLORS[0]),
          }
        })

        const weightedSum = weighted.reduce((sum, segment) => sum + segment.amount, 0)
        const normalizedSegments = (weightedSum > 0 ? weighted : weighted.map(segment => ({ ...segment, amount: 0 })))
          .map(segment => ({
            ...segment,
            amount: weightedSum > 0 ? (segment.amount / weightedSum) * coveredTotal : 0,
          }))
          .filter(segment => segment.amount > 0.005)

        if (uncoveredTotal > 0.005) {
          normalizedSegments.push({
            label: 'Uncovered',
            amount: uncoveredTotal,
            color: UNCOVERED_COLOR,
          })
        }

        return {
          category,
          totalMonthly: data.totalMonthly,
          segments: normalizedSegments.sort((left, right) => right.amount - left.amount),
        }
      })
      .filter(chart => chart.totalMonthly > 0.005)
      .sort((left, right) => right.totalMonthly - left.totalMonthly)
  }, [expenseResults, expenses, people, personColorById, totals])
")

$content = $content.Replace(
"                  <div className=""space-y-1"">
                    <Label>Monthly Net</Label>",
"                  <div className=""space-y-1.5"">
                    <Label>Weekly Cap</Label>
                    <Input
                      inputMode=""decimal""
                      value={person.weeklyCap}
                      placeholder=""Optional""
                      onChange={event => updatePerson(person.id, { weeklyCap: event.target.value })}
                    />
                  </div>
                  <div className=""space-y-1"">
                    <Label>Monthly Net</Label>")

$content = $content.Replace(
"        <Card>
          <CardHeader>
            <div className=""space-y-1"">
              <CardTitle className=""text-sm uppercase tracking-wide text-muted-foreground"">Expenses</CardTitle>
              <p className=""text-sm text-muted-foreground"">
                Cadence accepts values like <span className=""font-medium text-foreground"">monthly</span>,
                <span className=""font-medium text-foreground""> every 2 weeks</span>,
                <span className=""font-medium text-foreground""> quarterly</span>, or
                <span className=""font-medium text-foreground""> every 33 weeks</span>.
              </p>
            </div>
          </CardHeader>",
"        {categoryCharts.length > 0 && (
          <Card>
            <CardHeader>
              <div className=""space-y-1"">
                <CardTitle className=""text-sm uppercase tracking-wide text-muted-foreground"">Category Breakdown</CardTitle>
                <p className=""text-sm text-muted-foreground"">
                  Monthly pie charts showing how each category is split across people.
                  {totals.uncoveredMonthly > 0 && ' Uncovered portions are shown in gray.'}
                </p>
              </div>
            </CardHeader>
            <CardContent>
              <div className=""grid gap-4 md:grid-cols-2 xl:grid-cols-3"">
                {categoryCharts.map(chart => (
                  <div key={chart.category} className=""rounded-xl border border-border/70 bg-card/40 p-4"">
                    <div className=""mb-4 flex items-start justify-between gap-3"">
                      <div>
                        <h3 className=""text-sm font-semibold text-foreground"">{chart.category}</h3>
                        <p className=""text-xs text-muted-foreground"">{formatMoney(chart.totalMonthly)} / month</p>
                      </div>
                    </div>
                    <div className=""flex flex-col gap-4 lg:flex-row lg:items-center"">
                      <PieChart segments={chart.segments} />
                      <div className=""space-y-2"">
                        {chart.segments.map(segment => (
                          <div key={`${chart.category}-${segment.label}`} className=""flex items-center gap-2 text-sm"">
                            <span
                              className=""h-3 w-3 shrink-0 rounded-full border border-black/20""
                              style={{ backgroundColor: segment.color }}
                            />
                            <span className=""min-w-[7rem] text-muted-foreground"">{segment.label}</span>
                            <span className=""font-medium text-foreground"">{formatMoney(segment.amount)}</span>
                          </div>
                        ))}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </CardContent>
          </Card>
        )}

        <Card>
          <CardHeader>
            <div className=""space-y-1"">
              <CardTitle className=""text-sm uppercase tracking-wide text-muted-foreground"">Expenses</CardTitle>
              <p className=""text-sm text-muted-foreground"">
                Cadence accepts values like <span className=""font-medium text-foreground"">monthly</span>,
                <span className=""font-medium text-foreground""> every 2 weeks</span>,
                <span className=""font-medium text-foreground""> quarterly</span>, or
                <span className=""font-medium text-foreground""> every 33 weeks</span>.
              </p>
            </div>
          </CardHeader>")

Set-Content -LiteralPath $path -Value $content
