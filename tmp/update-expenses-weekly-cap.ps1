$path = 'D:\Code\dev-ui\src\pages\ExpensesPage.tsx'
$content = Get-Content -Raw $path

$content = $content.Replace(
"type Person = {
  id: string
  name: string
  yearlyGross: string
  yearlyNet: string
}",
"type Person = {
  id: string
  name: string
  yearlyGross: string
  yearlyNet: string
  weeklyCap: string
}")

$content = $content.Replace(
"type ExpenseComputation = {
  monthlyAmount: number
  weeklyAmount: number
  annualAmount: number
  sharesByPersonId: Record<string, number>
  cadenceError: string | null
}",
"type ExpenseComputation = {
  monthlyAmount: number
  weeklyAmount: number
  annualAmount: number
  sharesByPersonId: Record<string, number>
  cadenceError: string | null
}

type CappedAllocation = {
  expectedByPersonId: Record<string, number>
  cappedWeeklyByPersonId: Record<string, number | null>
  uncappedMonthlyByPersonId: Record<string, number>
  uncappedWeeklyByPersonId: Record<string, number>
  uncoveredMonthly: number
}")

$content = $content.Replace(
"    { id: 'al', name: 'Al', yearlyGross: '', yearlyNet: '' },
    { id: 'tito', name: 'Tito', yearlyGross: '', yearlyNet: '' },
    { id: 'mom', name: 'Mom', yearlyGross: '', yearlyNet: '' },",
"    { id: 'al', name: 'Al', yearlyGross: '', yearlyNet: '', weeklyCap: '' },
    { id: 'tito', name: 'Tito', yearlyGross: '', yearlyNet: '', weeklyCap: '' },
    { id: 'mom', name: 'Mom', yearlyGross: '', yearlyNet: '', weeklyCap: '' },")

$content = $content.Replace(
"          yearlyGross: typeof person?.yearlyGross === 'string' ? person.yearlyGross : '',
          yearlyNet: typeof person?.yearlyNet === 'string' ? person.yearlyNet : '',",
"          yearlyGross: typeof person?.yearlyGross === 'string' ? person.yearlyGross : '',
          yearlyNet: typeof person?.yearlyNet === 'string' ? person.yearlyNet : '',
          weeklyCap: typeof person?.weeklyCap === 'string' ? person.weeklyCap : '',")

$content = $content.Replace(
"function personDisplayName(person: Person, index: number): string {
  return person.name.trim() || `Person ${index + 1}`
}",
"function computeCappedAllocation(
  people: Person[],
  uncappedMonthlyByPersonId: Record<string, number>,
  totalMonthlyExpenses: number
): CappedAllocation {
  const cappedWeeklyByPersonId = Object.fromEntries(
    people.map(person => {
      const cap = parseAmount(person.weeklyCap)
      return [person.id, cap > 0 ? cap : null]
    })
  ) as Record<string, number | null>

  const capacitiesByPersonId = Object.fromEntries(
    people.map(person => {
      const weeklyCap = cappedWeeklyByPersonId[person.id]
      return [person.id, weeklyCap == null ? Number.POSITIVE_INFINITY : (weeklyCap * WEEKS_PER_YEAR) / MONTHS_PER_YEAR]
    })
  ) as Record<string, number>

  const expectedByPersonId = Object.fromEntries(people.map(person => [person.id, 0])) as Record<string, number>
  const unresolvedIds = new Set(people.map(person => person.id))
  let remainingMonthly = totalMonthlyExpenses

  while (unresolvedIds.size > 0 && remainingMonthly > 0.000001) {
    const activeIds = Array.from(unresolvedIds)
    const weightSum = activeIds.reduce((sum, personId) => sum + Math.max(uncappedMonthlyByPersonId[personId] ?? 0, 0), 0)
    const fallbackWeight = weightSum <= 0 ? 1 / activeIds.length : 0
    let cappedSomeoneThisPass = false

    for (const personId of activeIds) {
      const weight = weightSum > 0 ? Math.max(uncappedMonthlyByPersonId[personId] ?? 0, 0) / weightSum : fallbackWeight
      const provisional = remainingMonthly * weight
      const capacity = capacitiesByPersonId[personId]

      if (provisional >= capacity - 0.000001) {
        expectedByPersonId[personId] = capacity
        remainingMonthly -= capacity
        unresolvedIds.delete(personId)
        cappedSomeoneThisPass = true
      }
    }

    if (!cappedSomeoneThisPass) {
      const finalIds = Array.from(unresolvedIds)
      const finalWeightSum = finalIds.reduce((sum, personId) => sum + Math.max(uncappedMonthlyByPersonId[personId] ?? 0, 0), 0)
      const finalFallbackWeight = finalWeightSum <= 0 ? 1 / finalIds.length : 0

      for (const personId of finalIds) {
        const weight = finalWeightSum > 0 ? Math.max(uncappedMonthlyByPersonId[personId] ?? 0, 0) / finalWeightSum : finalFallbackWeight
        expectedByPersonId[personId] = remainingMonthly * weight
      }

      remainingMonthly = 0
      unresolvedIds.clear()
    }
  }

  return {
    expectedByPersonId,
    cappedWeeklyByPersonId,
    uncappedMonthlyByPersonId,
    uncappedWeeklyByPersonId: Object.fromEntries(
      people.map(person => [person.id, (uncappedMonthlyByPersonId[person.id] ?? 0) * MONTHS_PER_YEAR / WEEKS_PER_YEAR])
    ),
    uncoveredMonthly: Math.max(remainingMonthly, 0),
  }
}

function personDisplayName(person: Person, index: number): string {
  return person.name.trim() || `Person ${index + 1}`
}")

$content = $content.Replace(
"    const expectedByPersonId = Object.fromEntries(people.map(person => [person.id, 0]))
    const actualPaidByPersonId = Object.fromEntries(people.map(person => [person.id, 0]))
",
"    const uncappedExpectedByPersonId = Object.fromEntries(people.map(person => [person.id, 0])) as Record<string, number>
    const actualPaidByPersonId = Object.fromEntries(people.map(person => [person.id, 0])) as Record<string, number>
")

$content = $content.Replace(
"        expectedByPersonId[person.id] += result.sharesByPersonId[person.id] ?? 0",
"        uncappedExpectedByPersonId[person.id] += result.sharesByPersonId[person.id] ?? 0")

$content = $content.Replace(
"    return {
      totalMonthlyExpenses,
      totalWeeklyExpenses,
      expectedByPersonId,
      actualPaidByPersonId,
    }",
"    const cappedAllocation = computeCappedAllocation(people, uncappedExpectedByPersonId, totalMonthlyExpenses)

    return {
      totalMonthlyExpenses,
      totalWeeklyExpenses,
      actualPaidByPersonId,
      ...cappedAllocation,
    }")

$content = $content.Replace(
"      { id: makeId('person'), name: '', yearlyGross: '', yearlyNet: '' },",
"      { id: makeId('person'), name: '', yearlyGross: '', yearlyNet: '', weeklyCap: '' },")

$content = $content.Replace(
"              <p>
            The page stores your values in this browser automatically, converts every expense into weekly and monthly amounts, and
            recalculates the split summary as you edit.
          </p>",
"              <p>
            Weekly caps are optional. If someone hits their cap, the rest gets redistributed automatically to the others.
          </p>")

$content = $content.Replace(
"                <div key={person.id} className=""grid gap-3 rounded-lg border border-border/70 p-4 md:grid-cols-[minmax(0,1fr)_150px_150px_130px_auto] md:items-end"">",
"                <div key={person.id} className=""grid gap-3 rounded-lg border border-border/70 p-4 md:grid-cols-[minmax(0,1fr)_130px_130px_130px_130px_auto] md:items-end"">")

$content = $content.Replace(
"                <div className=""space-y-1"">
                  <Label>Monthly Net</Label>",
"                <div className=""space-y-1.5"">
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
"                <TableRow>
                  <TableCell>Percentage</TableCell>
                  {people.map(person => <TableCell key={person.id}>{formatPercent((incomeShares[person.id] ?? 0) * 100)}</TableCell>)}
                  <TableCell />
                </TableRow>
                <TableRow>
                  <TableCell>Expected Split Per Month</TableCell>
                  {people.map(person => <TableCell key={person.id}>{formatMoney(totals.expectedByPersonId[person.id] ?? 0)}</TableCell>)}
                  <TableCell>{formatMoney(totals.totalMonthlyExpenses)}</TableCell>
                </TableRow>",
"                <TableRow>
                  <TableCell>Income Percentage</TableCell>
                  {people.map(person => <TableCell key={person.id}>{formatPercent((incomeShares[person.id] ?? 0) * 100)}</TableCell>)}
                  <TableCell />
                </TableRow>
                <TableRow>
                  <TableCell>Weekly Cap</TableCell>
                  {people.map(person => <TableCell key={person.id}>{totals.cappedWeeklyByPersonId[person.id] == null ? 'No cap' : formatMoney(totals.cappedWeeklyByPersonId[person.id] ?? 0)}</TableCell>)}
                  <TableCell />
                </TableRow>
                <TableRow>
                  <TableCell>Expected Split Per Month</TableCell>
                  {people.map(person => <TableCell key={person.id}>{formatMoney(totals.expectedByPersonId[person.id] ?? 0)}</TableCell>)}
                  <TableCell>{formatMoney(totals.totalMonthlyExpenses - totals.uncoveredMonthly)}</TableCell>
                </TableRow>
                <TableRow>
                  <TableCell>Expected Split Per Week</TableCell>
                  {people.map(person => <TableCell key={person.id}>{formatMoney(((totals.expectedByPersonId[person.id] ?? 0) * MONTHS_PER_YEAR) / WEEKS_PER_YEAR)}</TableCell>)}
                  <TableCell>{formatMoney(totals.totalWeeklyExpenses - ((totals.uncoveredMonthly * MONTHS_PER_YEAR) / WEEKS_PER_YEAR))}</TableCell>
                </TableRow>")

$content = $content.Replace(
"                </TableRow>
              </TableBody>
            </Table>",
"                </TableRow>
                {totals.uncoveredMonthly > 0 && (
                  <TableRow>
                    <TableCell>Uncovered Due To Caps</TableCell>
                    {people.map(person => <TableCell key={person.id}>-</TableCell>)}
                    <TableCell>{formatMoney(totals.uncoveredMonthly)}</TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>")

Set-Content -LiteralPath $path -Value $content

