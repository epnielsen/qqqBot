"""
ETF Price Response Lag Analysis
===============================
Measures PRICE RESPONSE LAG: when QQQ makes a directional move,
how long until TQQQ/SQQQ fully reflect the expected 3x response?

This is impulse-response analysis, NOT tick-timing alignment:
  1. Rolling leverage ratio: over same time windows, QQQ% vs ETF%
  2. Price response lag: detect QQQ moves, measure ETF catch-up time
  3. Stop-trigger scenario: when QQQ drops from HWM, what has ETF dropped?
"""

import csv
import os
import sys
from datetime import datetime, timezone
from bisect import bisect_left
from statistics import median, mean

DATA_DIR = r"C:\dev\TradeEcosystem\data\market"
DATES = ["20260209", "20260210", "20260211", "20260212", "20260213"]


def load_ticks(filepath):
    """Load CSV ticks, return list of (epoch_ms, price) tuples sorted by time."""
    ticks = []
    with open(filepath, "r") as f:
        reader = csv.reader(f)
        header = next(reader)  # skip header
        for row in reader:
            ts_str = row[0]
            price = float(row[2])
            # Parse ISO 8601 timestamp
            dt = datetime.fromisoformat(ts_str)
            epoch_ms = int(dt.timestamp() * 1000)
            ticks.append((epoch_ms, price))
    ticks.sort(key=lambda x: x[0])
    return ticks


def find_nearest_idx(ticks, target_ms):
    """Binary search for nearest tick to target_ms."""
    # ticks is list of (epoch_ms, price)
    n = len(ticks)
    if n == 0:
        return 0
    # bisect on epoch_ms
    lo, hi = 0, n - 1
    while lo < hi:
        mid = (lo + hi) // 2
        if ticks[mid][0] < target_ms:
            lo = mid + 1
        else:
            hi = mid
    # Check if lo-1 is closer
    if lo > 0 and abs(ticks[lo][0] - target_ms) > abs(ticks[lo - 1][0] - target_ms):
        return lo - 1
    return lo


def price_at_time(ticks, ms):
    """Return (epoch_ms, price) of nearest tick to ms."""
    idx = find_nearest_idx(ticks, ms)
    return ticks[idx]


def percentile(sorted_list, pct):
    """Get percentile from a sorted list. pct in 0-100."""
    if not sorted_list:
        return 0
    idx = int(len(sorted_list) * pct / 100)
    idx = max(0, min(idx, len(sorted_list) - 1))
    return sorted_list[idx]


def filter_market_hours(ticks, date_str):
    """Filter ticks to market hours (14:30-21:00 UTC)."""
    year = int(date_str[:4])
    month = int(date_str[4:6])
    day = int(date_str[6:8])
    mkt_open = datetime(year, month, day, 14, 30, 0, tzinfo=timezone.utc)
    mkt_close = datetime(year, month, day, 21, 0, 0, tzinfo=timezone.utc)
    open_ms = int(mkt_open.timestamp() * 1000)
    close_ms = int(mkt_close.timestamp() * 1000)
    return [(ms, p) for ms, p in ticks if open_ms <= ms <= close_ms]


def run_analysis():
    print("=" * 90)
    print("ETF PRICE RESPONSE LAG ANALYSIS")
    print("When QQQ makes a directional move, how long until TQQQ/SQQQ reflect 3x?")
    print("=" * 90)
    print()

    for date in DATES:
        print("-" * 70)
        print(f"DATE: {date}")
        print("-" * 70)

        qqq_file = os.path.join(DATA_DIR, f"{date}_market_data_QQQ.csv")
        tqqq_file = os.path.join(DATA_DIR, f"{date}_market_data_TQQQ.csv")
        sqqq_file = os.path.join(DATA_DIR, f"{date}_market_data_SQQQ.csv")

        if not os.path.exists(qqq_file):
            print("  SKIP: Missing data")
            continue

        print("  Loading ticks...")
        qqq = filter_market_hours(load_ticks(qqq_file), date)
        tqqq = filter_market_hours(load_ticks(tqqq_file), date)
        sqqq = filter_market_hours(load_ticks(sqqq_file), date)

        print(f"  Ticks: QQQ={len(qqq)}  TQQQ={len(tqqq)}  SQQQ={len(sqqq)}")

        if len(qqq) < 100 or len(tqqq) < 100 or len(sqqq) < 100:
            print("  SKIP: Too few ticks")
            continue

        # =========================================================
        # PART 1: ROLLING LEVERAGE RATIO
        # For sampled QQQ ticks, compare QQQ % change vs ETF % change
        # over rolling windows of 5s, 10s, 30s, 60s
        # =========================================================
        print()
        print("  [1/3] Rolling leverage ratio (QQQ move vs ETF move over same time window)...")
        print()

        for window_sec in [5, 10, 30, 60]:
            window_ms = window_sec * 1000
            t_ratios = []
            s_ratios = []

            step = max(1, len(qqq) // 500)
            for i in range(0, len(qqq), step):
                q0_ms, q0_p = qqq[i]
                q1_ms, q1_p = price_at_time(qqq, q0_ms + window_ms)
                actual_gap = q1_ms - q0_ms
                if actual_gap < window_ms * 0.5 or actual_gap > window_ms * 1.5:
                    continue

                q_pct = (q1_p - q0_p) / q0_p * 100
                if abs(q_pct) < 0.005:
                    continue  # skip noise

                t0_ms, t0_p = price_at_time(tqqq, q0_ms)
                t1_ms, t1_p = price_at_time(tqqq, q0_ms + window_ms)
                t_pct = (t1_p - t0_p) / t0_p * 100

                s0_ms, s0_p = price_at_time(sqqq, q0_ms)
                s1_ms, s1_p = price_at_time(sqqq, q0_ms + window_ms)
                s_pct = (s1_p - s0_p) / s0_p * 100

                if q_pct != 0:
                    t_ratios.append(t_pct / q_pct)
                    s_ratios.append(s_pct / q_pct)

            if len(t_ratios) > 10:
                t_sorted = sorted(t_ratios)
                s_sorted = sorted(s_ratios)

                t_med = round(percentile(t_sorted, 50), 3)
                t_p10 = round(percentile(t_sorted, 10), 3)
                t_p90 = round(percentile(t_sorted, 90), 3)
                s_med = round(percentile(s_sorted, 50), 3)
                s_p10 = round(percentile(s_sorted, 10), 3)
                s_p90 = round(percentile(s_sorted, 90), 3)

                t_mae = round(mean(abs(r - 3.0) for r in t_ratios), 3)
                s_mae = round(mean(abs(r - (-3.0)) for r in s_ratios), 3)

                print(f"    {window_sec:3d}s window (n={len(t_ratios):4d}):  "
                      f"TQQQ/QQQ med={t_med:6.3f} [p10={t_p10:6.3f} p90={t_p90:6.3f}] MAE={t_mae:5.3f}  |  "
                      f"SQQQ/QQQ med={s_med:7.3f} [p10={s_p10:7.3f} p90={s_p90:7.3f}] MAE={s_mae:5.3f}")
            else:
                print(f"    {window_sec:3d}s window: insufficient data points ({len(t_ratios)})")

        # =========================================================
        # PART 2: PRICE RESPONSE LAG (the key analysis)
        # Detect QQQ directional moves, then watch ETFs forward
        # =========================================================
        print()
        print("  [2/3] Price response lag analysis...")
        print("        Detecting QQQ moves >0.03% over 10s, then watching ETF catch-up...")
        print()

        move_threshold_pct = 0.03
        move_window_ms = 10000  # 10s window to detect QQQ moves
        watch_steps = [0, 1000, 2000, 5000, 10000, 20000, 30000]

        move_events = []
        step = max(1, len(qqq) // 1000)

        for i in range(0, len(qqq), step):
            q0_ms, q0_p = qqq[i]
            q1_ms, q1_p = price_at_time(qqq, q0_ms + move_window_ms)
            gap = q1_ms - q0_ms
            if gap < 5000 or gap > 15000:
                continue

            q_pct = (q1_p - q0_p) / q0_p * 100
            if abs(q_pct) < move_threshold_pct:
                continue

            direction = "UP" if q_pct > 0 else "DOWN"
            expected_tqqq = q_pct * 3
            expected_sqqq = q_pct * -3

            # Get ETF position at start of QQQ move
            t0_ms, t0_p = price_at_time(tqqq, q0_ms)
            s0_ms, s0_p = price_at_time(sqqq, q0_ms)

            # Track ETF response at multiple forward offsets from QQQ move END
            t_responses = {}
            s_responses = {}

            for offset in watch_steps:
                check_ms = q0_ms + move_window_ms + offset
                t_check = price_at_time(tqqq, check_ms)
                s_check = price_at_time(sqqq, check_ms)

                t_actual_pct = (t_check[1] - t0_p) / t0_p * 100
                s_actual_pct = (s_check[1] - s0_p) / s0_p * 100

                t_responses[offset] = t_actual_pct
                s_responses[offset] = s_actual_pct

            # At the instant QQQ completes its move (offset 0)
            t_at_end = t_responses[0]
            s_at_end = s_responses[0]

            t_achieved = (t_at_end / expected_tqqq * 100) if expected_tqqq != 0 else 100
            s_achieved = (s_at_end / expected_sqqq * 100) if expected_sqqq != 0 else 100

            # Find first offset where TQQQ achieves >= 90% of expected
            t_catchup_ms = -1
            for offset in watch_steps:
                t_pct = t_responses[offset]
                ratio = (t_pct / expected_tqqq * 100) if expected_tqqq != 0 else 100
                if ratio >= 90:
                    t_catchup_ms = offset
                    break

            move_events.append({
                "direction": direction,
                "qqq_move_pct": round(q_pct, 4),
                "expected_tqqq": round(expected_tqqq, 4),
                "tqqq_at_end": round(t_at_end, 4),
                "t_achieved_pct": round(t_achieved, 1),
                "t_catchup_ms": t_catchup_ms,
                "expected_sqqq": round(expected_sqqq, 4),
                "sqqq_at_end": round(s_at_end, 4),
                "s_achieved_pct": round(s_achieved, 1),
            })

        if move_events:
            t_achieved_all = sorted(e["t_achieved_pct"] for e in move_events)
            s_achieved_all = sorted(e["s_achieved_pct"] for e in move_events)

            t_ach_med = round(percentile(t_achieved_all, 50), 1)
            t_ach_p10 = round(percentile(t_achieved_all, 10), 1)
            t_ach_p90 = round(percentile(t_achieved_all, 90), 1)
            s_ach_med = round(percentile(s_achieved_all, 50), 1)
            s_ach_p10 = round(percentile(s_achieved_all, 10), 1)
            s_ach_p90 = round(percentile(s_achieved_all, 90), 1)

            print(f"    QQQ moves detected: {len(move_events)}")
            print(f"    At instant QQQ move completes, ETF has achieved:")
            print(f"      TQQQ: median={t_ach_med}% of expected 3x  [p10={t_ach_p10}%  p90={t_ach_p90}%]")
            print(f"      SQQQ: median={s_ach_med}% of expected -3x [p10={s_ach_p10}%  p90={s_ach_p90}%]")

            # Catch-up analysis
            catchups = [e for e in move_events if e["t_catchup_ms"] >= 0]
            no_catchup = [e for e in move_events if e["t_catchup_ms"] < 0]
            print()
            print("    TQQQ catch-up to 90% of expected:")
            print(f"      Immediate (0ms): {sum(1 for e in catchups if e['t_catchup_ms'] == 0)} / {len(move_events)}")
            print(f"      Within 1s:  {sum(1 for e in catchups if e['t_catchup_ms'] <= 1000)} / {len(move_events)}")
            print(f"      Within 5s:  {sum(1 for e in catchups if e['t_catchup_ms'] <= 5000)} / {len(move_events)}")
            print(f"      Within 10s: {sum(1 for e in catchups if e['t_catchup_ms'] <= 10000)} / {len(move_events)}")
            print(f"      Within 30s: {sum(1 for e in catchups if e['t_catchup_ms'] <= 30000)} / {len(move_events)}")
            print(f"      Never (in 30s window): {len(no_catchup)} / {len(move_events)}")

            # Worst TQQQ underperformance
            worst_t = sorted(move_events, key=lambda e: e["t_achieved_pct"])[:5]
            print()
            print("    Worst TQQQ underperformance events (lowest % of expected 3x at QQQ move end):")
            print(f"      {'Dir':>6s} {'QQQ%':>10s} {'Exp TQQQ%':>10s} {'Act TQQQ%':>10s} {'Achieved%':>10s} {'CatchUp':>10s}")
            for w in worst_t:
                cu = f"{w['t_catchup_ms']}ms" if w["t_catchup_ms"] >= 0 else ">30s"
                print(f"      {w['direction']:>6s} {w['qqq_move_pct']:>10.4f} {w['expected_tqqq']:>10.4f} "
                      f"{w['tqqq_at_end']:>10.4f} {w['t_achieved_pct']:>10.1f} {cu:>10s}")

            # Worst SQQQ underperformance
            worst_s = sorted(move_events, key=lambda e: e["s_achieved_pct"])[:5]
            print()
            print("    Worst SQQQ underperformance events:")
            print(f"      {'Dir':>6s} {'QQQ%':>10s} {'Exp SQQQ%':>10s} {'Act SQQQ%':>10s} {'Achieved%':>10s}")
            for w in worst_s:
                print(f"      {w['direction']:>6s} {w['qqq_move_pct']:>10.4f} {w['expected_sqqq']:>10.4f} "
                      f"{w['sqqq_at_end']:>10.4f} {w['s_achieved_pct']:>10.1f}")
        else:
            print("    No QQQ moves detected above threshold")

        # =========================================================
        # PART 3: STOP-RELEVANT SCENARIO
        # When QQQ drops past a trailing stop threshold from HWM,
        # measure the ETF's simultaneous drop vs expected 3x
        # =========================================================
        print()
        print("  [3/3] Stop-trigger scenario analysis...")
        print("        When QQQ drops 0.2%/0.5% from recent high, what has TQQQ dropped?")
        print()

        for stop_pct in [0.2, 0.5]:
            hwm = qqq[0][1]
            t_hwm = tqqq[0][1]
            trigger_events = []
            cooldown_ms = 60000
            last_trigger_ms = 0

            step = max(1, len(qqq) // 3000)
            for i in range(1, len(qqq), step):
                q_ms, q_p = qqq[i]

                # Get TQQQ price at same time
                t_cur = price_at_time(tqqq, q_ms)

                # Update HWMs
                if q_p > hwm:
                    hwm = q_p
                    t_hwm = t_cur[1]
                if t_cur[1] > t_hwm:
                    t_hwm = t_cur[1]

                # Check if QQQ has dropped stop_pct% from HWM
                q_drop = (hwm - q_p) / hwm * 100
                if q_drop >= stop_pct and q_drop < (stop_pct + 0.05) and (q_ms - last_trigger_ms) > cooldown_ms:
                    t_drop = (t_hwm - t_cur[1]) / t_hwm * 100
                    ratio = t_drop / q_drop if q_drop > 0 else 0

                    trigger_events.append({
                        "qqq_drop": round(q_drop, 4),
                        "tqqq_drop": round(t_drop, 4),
                        "ratio": round(ratio, 3),
                        "tick_gap_ms": abs(t_cur[0] - q_ms),
                    })

                    last_trigger_ms = q_ms
                    hwm = q_p  # reset after trigger
                    t_hwm = t_cur[1]

            if trigger_events:
                ratios = sorted(e["ratio"] for e in trigger_events)
                r_med = round(percentile(ratios, 50), 3)
                r_min = round(ratios[0], 3)
                r_max = round(ratios[-1], 3)
                r_mean = round(mean(ratios), 3)

                print(f"    Stop at {stop_pct}% (n={len(trigger_events)}):  "
                      f"TQQQ_drop/QQQ_drop ratio:  mean={r_mean}  median={r_med}  [min={r_min}, max={r_max}]")
                print(f"      Expected ratio: ~3.0.  <3.0 = TQQQ undershoots (stop fires before ETF reflects full loss)")
                print(f"                             >3.0 = TQQQ overshoots (ETF loss larger than QQQ stop implies)")
            else:
                print(f"    Stop at {stop_pct}%: no trigger events")

        print()

    # Summary
    print("=" * 90)
    print("KEY QUESTIONS ANSWERED")
    print("=" * 90)
    print()
    print("Q: When QQQ makes a directional move, do TQQQ/SQQQ respond immediately at 3x?")
    print("   Look at Part 2 'achieved %' - if median is ~100%, response is instantaneous.")
    print("   If median is < 90%, there is meaningful price response lag.")
    print()
    print("Q: When a QQQ-based trailing stop fires, is the ETF P/L what we'd expect?")
    print("   Look at Part 3 'stop-trigger ratio' - if ratio is ~3.0, stop domain doesn't matter.")
    print("   If ratio deviates (especially < 2.5 or > 3.5), ETF-based stops would be more accurate.")
    print()
    print("Q: Does the leverage ratio hold up on fast moves?")
    print("   Look at Part 1 short windows (5s, 10s) - tight p10/p90 range = good tracking.")
    print("   Wide spread = ETFs diverge on fast moves and may need different stop logic.")


if __name__ == "__main__":
    try:
        run_analysis()
    except Exception as e:
        import traceback
        print(f"\nERROR: {e}")
        traceback.print_exc()
