$mr = Import-Csv "sweep_results/ph_mr/results.csv"
$tr = Import-Csv "sweep_results/ph_trend/results.csv"

$mrDates = $mr | Group-Object Date | Sort-Object Name | ForEach-Object { 
    $pnl = ($_.Group | Measure-Object RealizedPnL -Average).Average
    $wins = ($_.Group | Where-Object { [double]$_.RealizedPnL -gt 0 }).Count
    [PSCustomObject]@{ Date=$_.Name; MeanPnL=[math]::Round($pnl,2); WinPct=[math]::Round(100*$wins/$_.Group.Count,0) }
}
$trDates = $tr | Group-Object Date | Sort-Object Name | ForEach-Object { 
    $pnl = ($_.Group | Measure-Object RealizedPnL -Average).Average
    $wins = ($_.Group | Where-Object { [double]$_.RealizedPnL -gt 0 }).Count
    [PSCustomObject]@{ Date=$_.Name; MeanPnL=[math]::Round($pnl,2); WinPct=[math]::Round(100*$wins/$_.Group.Count,0) }
}

$mrArr = @($mrDates); $trArr = @($trDates)
Write-Host ""
Write-Host ("  {0,-12} {1,10} {2,10} {3,10} {4,8}" -f "Date","MR","Trend","Diff","Winner")
Write-Host ("  {0,-12} {1,10} {2,10} {3,10} {4,8}" -f "----","--","-----","----","------")
for ($i = 0; $i -lt $mrArr.Count; $i++) {
    $mrP = $mrArr[$i].MeanPnL; $trP = $trArr[$i].MeanPnL
    $diff = [math]::Round($mrP - $trP, 2)
    $w = if ($diff -gt 1) {"MR"} elseif ($diff -lt -1) {"Trend"} else {"TIE"}
    Write-Host ("  {0,-12} {1,10} {2,10} {3,10} {4,8}" -f $mrArr[$i].Date, $mrP, $trP, $diff, $w)
}
$mrT = [math]::Round(($mrArr | Measure-Object MeanPnL -Sum).Sum, 2)
$trT = [math]::Round(($trArr | Measure-Object MeanPnL -Sum).Sum, 2)
Write-Host ("  {0,-12} {1,10} {2,10} {3,10}" -f "TOTAL", $mrT, $trT, [math]::Round($mrT-$trT,2))
Write-Host ""
$mrWins = ($mrArr | Where-Object {$_.MeanPnL -gt 0}).Count
$trWins = ($trArr | Where-Object {$_.MeanPnL -gt 0}).Count
Write-Host "  MR:    Mean=`$$([math]::Round($mrT/14,2))/day  Wins=$mrWins/14  StdDev=`$309"
Write-Host "  Trend: Mean=`$$([math]::Round($trT/14,2))/day  Wins=$trWins/14  StdDev=`$327"
