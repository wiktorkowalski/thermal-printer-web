import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { Printer, RefreshCw, Search } from "lucide-react";
import { cn, when } from "@/lib/utils";
import { printerApi, type PrintError } from "@/lib/api";
import { DOTS_PER_MM } from "@/lib/paper";
import { isBlocked } from "@/lib/printer-light";
import { SEARCH_MAX_QUERY_LENGTH, SEARCH_MIN_QUERY_LENGTH, STATS_MAX_DAYS } from "@/lib/printer-limits";
import type { PrinterStatusState } from "@/hooks/use-printer-status";
import { usePrintJob } from "@/hooks/use-print-job";
import type { PapercutLedger, PrintJobDayStats, PrintJobSearchHit, PrintJobStats } from "@/types/printer";
import { Toast } from "@/components/print-chrome";
import { Segmented, inputClass } from "@/components/editor/controls";

/**
 * loading: the read runs. off: the server keeps no journal. busy: the server runs other journal queries.
 * error: the read failed. Each of the last three has its own text.
 */
type Fault = "off" | "busy" | "error";
type Read<T> = { status: "loading" } | { status: "ready"; data: T } | { status: Fault };

function faultOf(error: PrintError): Fault {
  return error.type === "journal-off" ? "off" : error.type === "busy" ? "busy" : "error";
}

const FAULT_TEXT: Record<Fault, string> = {
  off: "The server keeps no print history, so there is nothing to show here. Prints still go out.",
  busy: "The server is busy with other journal queries. Try again in a few seconds.",
  error: "This did not load. The server did not answer, or it cannot read its print history at the moment.",
};

const PERIODS = [
  { value: "7", label: "7 days" },
  { value: "30", label: "30 days" },
  { value: "90", label: "90 days" },
  { value: String(STATS_MAX_DAYS), label: "Year" },
];

// One search reads a limited number of prints. With no hit yet, the page asks for the next part by itself this many times.
const SEARCH_ROUNDS = 3;

const noteClass = "rounded-xl border border-dashed border-line-strong p-5 text-sm text-ink-2";
const pillButtonClass =
  "flex h-9 items-center gap-1.5 rounded-full border border-line-strong px-3.5 text-[13px] text-ink hover:bg-well disabled:opacity-40";

function paper(dots: number): string {
  const mm = dots / DOTS_PER_MM;
  return mm >= 1000 ? `${(mm / 1000).toFixed(2)} m` : `${Math.round(mm)} mm`;
}

function day(iso: string): string {
  return new Date(`${iso}T00:00:00Z`).toLocaleDateString([], { day: "numeric", month: "short", timeZone: "UTC" });
}

function FaultNote({ fault, onRetry }: { fault: Fault; onRetry: () => void }) {
  return (
    <div className={cn(noteClass, "flex flex-col items-start gap-3")} role="alert">
      {FAULT_TEXT[fault]}
      {fault !== "off" && (
        <button type="button" onClick={onRetry} className={pillButtonClass}>
          <RefreshCw className="size-3.5" aria-hidden="true" />
          Try again
        </button>
      )}
    </div>
  );
}

function Figure({ value, label }: { value: string; label: string }) {
  return (
    <div className="flex flex-col gap-1">
      <dd className="font-serif text-[44px] leading-none">{value}</dd>
      <dt className="text-[13px] text-ink-2">{label}</dt>
    </div>
  );
}

/** One bar per UTC day: its height is the number of prints of that day. */
function DayBars({ days }: { days: PrintJobDayStats[] }) {
  const most = Math.max(1, ...days.map((entry) => entry.printed));
  return (
    <div>
      <div
        role="img"
        aria-label={`Prints per day from ${day(days[0].day)} to ${day(days[days.length - 1].day)}, at most ${most} on one day`}
        className="flex h-28 items-end gap-px border-b border-line-strong"
      >
        {days.map((entry) => (
          <div
            key={entry.day}
            title={`${day(entry.day)}: ${entry.printed} printed, ${paper(entry.paperDots)}${entry.jobs > entry.printed ? `, ${entry.jobs - entry.printed} not printed` : ""}`}
            className="flex h-full min-w-0 flex-1 items-end"
          >
            <div
              className={cn("w-full rounded-t-[2px]", entry.printed > 0 ? "bg-ink" : "bg-line-strong")}
              style={{ height: entry.printed > 0 ? `max(3px, ${(entry.printed / most) * 100}%)` : entry.jobs > 0 ? "3px" : "0" }}
            />
          </div>
        ))}
      </div>
      <div className="mt-1.5 flex justify-between font-mono text-xs text-ink-3">
        <span>{day(days[0].day)}</span>
        <span>days in UTC</span>
        <span>{day(days[days.length - 1].day)}</span>
      </div>
    </div>
  );
}

function Statistics({ stats }: { stats: PrintJobStats }) {
  const { totals } = stats;
  if (totals.jobs === 0) return <div className={noteClass}>No print in this time. Pick a longer time, or print a note.</div>;

  const mostPaper = Math.max(1, ...stats.bySource.map((entry) => entry.paperDots));
  const others = stats.byResult.filter((entry) => entry.result !== "Printed");
  return (
    <div className="flex flex-col gap-8">
      <dl className="grid grid-cols-2 gap-x-6 gap-y-5 sm:grid-cols-4">
        <Figure value={String(totals.printed)} label={totals.printed === 1 ? "print" : "prints"} />
        <Figure value={paper(totals.paperDots)} label="of paper" />
        <Figure value={String(totals.reprints)} label={totals.reprints === 1 ? "reprint" : "reprints"} />
        <Figure value={String(totals.jobs - totals.printed)} label="not printed" />
      </dl>

      <DayBars days={stats.byDay} />

      <table className="w-full border-collapse text-sm">
        <caption className="sr-only">Prints and paper by source</caption>
        <thead>
          <tr className="border-b border-line-strong text-left text-[13px] text-ink-2">
            <th scope="col" className="py-2 pr-3 font-normal">
              Source
            </th>
            <th scope="col" className="py-2 pr-3 text-right font-normal">
              Prints
            </th>
            <th scope="col" className="py-2 text-right font-normal">
              Paper
            </th>
            <th scope="col" className="hidden w-[32%] py-2 pl-4 font-normal sm:table-cell">
              <span className="sr-only">Share of paper</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {stats.bySource.map((entry, index) => (
            <tr key={index} className="border-b border-line">
              <th scope="row" className="max-w-0 truncate py-2.5 pr-3 text-left font-mono text-[13px] font-normal">
                {entry.source ?? <span className="font-sans text-ink-3 italic">no source</span>}
              </th>
              <td className="py-2.5 pr-3 text-right font-mono text-[13px]">{entry.printed}</td>
              <td className="py-2.5 text-right font-mono text-[13px] whitespace-nowrap">{paper(entry.paperDots)}</td>
              <td className="hidden py-2.5 pl-4 sm:table-cell">
                <div className="h-2 rounded-full bg-well">
                  <div className="h-2 rounded-full bg-accent" style={{ width: `${(entry.paperDots / mostPaper) * 100}%` }} />
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {(stats.moreSources || others.length > 0) && (
        <p className="-mt-4 text-[13px] text-ink-2">
          {stats.moreSources && "Only the sources with the most jobs are listed. "}
          {others.length > 0 && `Not printed: ${others.map((entry) => `${entry.jobs} ${entry.result.toLowerCase()}`).join(", ")}.`}
        </p>
      )}
    </div>
  );
}

/** The snippet with its match marked. Printed text: React text nodes only. */
function Snippet({ text, query }: { text: string; query: string }) {
  const lower = text.toLowerCase();
  // A letter whose small form has another length would move the place of the match.
  const at = lower.length === text.length ? lower.indexOf(query.toLowerCase()) : -1;
  if (at < 0) return <>{text}</>;
  return (
    <>
      {text.slice(0, at)}
      <mark className="rounded-[3px] bg-accent/20 px-0.5 text-ink">{text.slice(at, at + query.length)}</mark>
      {text.slice(at + query.length)}
    </>
  );
}

interface SearchState {
  status: "idle" | "loading" | "ready" | Fault;
  query: string;
  hits: PrintJobSearchHit[];
  next: string | null;
}

function Section({ id, title, aside, children }: { id: string; title: string; aside?: ReactNode; children: ReactNode }) {
  return (
    <section aria-labelledby={id} className="flex flex-col gap-5 border-t border-line pt-8">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id={id} className="font-serif text-[30px] leading-none">
          {title}
        </h2>
        {aside}
      </div>
      {children}
    </section>
  );
}

export default function Journal({ printer }: { printer: PrinterStatusState }) {
  const job = usePrintJob(printer);
  const [period, setPeriod] = useState("30");
  const [stats, setStats] = useState<Read<PrintJobStats>>({ status: "loading" });
  const [ledger, setLedger] = useState<Read<PapercutLedger>>({ status: "loading" });
  const [text, setText] = useState("");
  const [search, setSearch] = useState<SearchState>({ status: "idle", query: "", hits: [], next: null });
  // An answer of an older read is dropped.
  const statsRead = useRef(0);
  const searchRead = useRef(0);

  const loadStats = useCallback(async (days: string) => {
    const mine = ++statsRead.current;
    setStats({ status: "loading" });
    try {
      const data = await printerApi.getStats(Number(days));
      if (mine === statsRead.current) setStats({ status: "ready", data });
    } catch (error) {
      if (mine === statsRead.current) setStats({ status: faultOf(error as PrintError) });
    }
  }, []);

  const loadLedger = useCallback(async () => {
    setLedger({ status: "loading" });
    try {
      setLedger({ status: "ready", data: await printerApi.getPapercuts() });
    } catch (error) {
      setLedger({ status: faultOf(error as PrintError) });
    }
  }, []);

  // One read after the other: the server runs few journal queries at a time.
  useEffect(() => {
    void loadStats("30").then(loadLedger);
  }, [loadStats, loadLedger]);

  const changePeriod = (value: string) => {
    setPeriod(value);
    void loadStats(value);
  };

  const runSearch = async (query: string, before: string | null, shown: PrintJobSearchHit[]) => {
    const mine = ++searchRead.current;
    setSearch({ status: "loading", query, hits: shown, next: before });
    try {
      let cursor = before;
      let hits = shown;
      for (let round = 0; round < SEARCH_ROUNDS; round++) {
        const page = await printerApi.searchJobs(query, cursor);
        if (mine !== searchRead.current) return;
        hits = [...hits, ...page.hits];
        cursor = page.next;
        if (page.hits.length > 0 || !cursor) break;
      }
      setSearch({ status: "ready", query, hits, next: cursor });
    } catch (error) {
      // The hits on screen stay; the button is there for another try.
      if (mine === searchRead.current) setSearch({ status: faultOf(error as PrintError), query, hits: shown, next: before });
    }
  };

  const query = text.trim();
  const canSearch = query.length >= SEARCH_MIN_QUERY_LENGTH;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (canSearch) void runSearch(query, null, []);
  };

  const searching = search.status === "loading";
  const searchFault = search.status === "off" || search.status === "busy" || search.status === "error" ? search.status : null;

  return (
    <main className="mx-auto flex w-full max-w-[920px] flex-col gap-10 px-4 pt-8 pb-24 md:px-8 md:pt-12">
      <header className="flex flex-col gap-2">
        <h1 className="font-serif text-[44px] leading-none md:text-[56px]">Journal</h1>
        <p className="max-w-[60ch] text-[15px] text-ink-2">Every print the server stored, from every device: how much, from where, and what it said.</p>
      </header>

      <Section
        id="journal-stats"
        title="Paper used"
        aside={<Segmented label="Time" value={period} options={PERIODS} onChange={changePeriod} className="w-full sm:w-[340px]" />}
      >
        <div aria-busy={stats.status === "loading"} className="min-h-24">
          {stats.status === "loading" && <div className={noteClass}>Counting the prints…</div>}
          {stats.status === "ready" && <Statistics stats={stats.data} />}
          {stats.status !== "loading" && stats.status !== "ready" && <FaultNote fault={stats.status} onRetry={() => void loadStats(period)} />}
        </div>
      </Section>

      <Section id="journal-search" title="Search">
        <form onSubmit={submit} role="search" className="flex gap-2">
          <label htmlFor="journal-query" className="sr-only">
            Text to find in the prints
          </label>
          <input
            id="journal-query"
            type="search"
            value={text}
            onChange={(event) => setText(event.target.value)}
            maxLength={SEARCH_MAX_QUERY_LENGTH}
            placeholder="Text that was printed"
            autoComplete="off"
            className={inputClass}
          />
          <button
            type="submit"
            disabled={!canSearch || searching}
            className="flex h-11 shrink-0 items-center gap-2 rounded-lg bg-ink px-4 text-sm text-on-ink disabled:opacity-40"
          >
            <Search className="size-4" aria-hidden="true" />
            Search
          </button>
        </form>
        {text.length > 0 && !canSearch && <p className="-mt-2 text-[13px] text-ink-2">Type at least {SEARCH_MIN_QUERY_LENGTH} characters.</p>}

        <div aria-live="polite" aria-busy={searching} className="flex flex-col gap-4">
          {search.status === "ready" && search.hits.length === 0 && (
            <div className={noteClass}>
              {search.next ? "No print with this text among the newest prints." : "No print holds this text."} Letter case does not matter; the text must match
              letter for letter.
            </div>
          )}
          {search.hits.length > 0 && (
            <ol className="flex flex-col">
              {search.hits.map(({ job: hit, snippet }) => {
                const title = hit.title || "Untitled";
                return (
                  <li key={hit.id} className="flex items-start justify-between gap-4 border-b border-line py-4 first:pt-0">
                    <div className="flex min-w-0 flex-col gap-1">
                      <span className="truncate font-semibold">{title}</span>
                      <span className="truncate font-mono text-xs text-ink-2">
                        {when(hit.createdAt)} · {hit.source || hit.transport}
                        {hit.result !== "Printed" && <span className="text-danger-text"> · not printed</span>}
                      </span>
                      <p className="text-sm break-words text-ink-2">
                        <Snippet text={snippet} query={search.query} />
                      </p>
                    </div>
                    <button
                      type="button"
                      disabled={!hit.canReprint || job.printing || isBlocked(printer)}
                      onClick={() => void job.reprint(hit.id)}
                      aria-label={`Reprint ${title}`}
                      className={cn(pillButtonClass, "shrink-0")}
                    >
                      <Printer className="size-3.5" aria-hidden="true" />
                      Reprint
                    </button>
                  </li>
                );
              })}
            </ol>
          )}
          {searching && <div className={noteClass}>Reading the prints…</div>}
          {searchFault && <FaultNote fault={searchFault} onRetry={() => void runSearch(search.query, search.next, search.hits)} />}
          {search.status === "ready" && search.next && (
            <button type="button" onClick={() => void runSearch(search.query, search.next, search.hits)} className={cn(pillButtonClass, "self-start")}>
              Search older prints
            </button>
          )}
        </div>
      </Section>

      <Section id="journal-papercuts" title="Papercuts">
        <p className="-mt-2 max-w-[60ch] text-sm text-ink-2">
          Strips that start with the line PAPERCUT, grouped by their next line. A papercut that keeps coming back shows up with a count.
        </p>
        <div aria-busy={ledger.status === "loading"}>
          {ledger.status === "loading" && <div className={noteClass}>Reading the strips…</div>}
          {ledger.status !== "loading" && ledger.status !== "ready" && <FaultNote fault={ledger.status} onRetry={() => void loadLedger()} />}
          {ledger.status === "ready" && ledger.data.papercuts.length === 0 && (
            <div className={noteClass}>No papercut strip yet. A strip counts when its first line is PAPERCUT.</div>
          )}
          {ledger.status === "ready" && ledger.data.papercuts.length > 0 && (
            <>
              <ol className="flex flex-col">
                {ledger.data.papercuts.map((entry) => (
                  <li key={entry.lastJobId} className="flex items-baseline gap-4 border-b border-line py-3">
                    <span className={cn("w-12 shrink-0 text-right font-serif text-[28px] leading-none", entry.count > 1 ? "text-accent-text" : "text-ink-3")}>
                      ×{entry.count}
                    </span>
                    <span className="min-w-0 grow text-[15px] break-words">{entry.subject}</span>
                    <span className="shrink-0 font-mono text-xs text-ink-2">
                      {entry.count > 1 ? `${when(entry.firstAt)} – ${when(entry.lastAt)}` : when(entry.lastAt)}
                    </span>
                  </li>
                ))}
              </ol>
              <p className="mt-3 text-[13px] text-ink-2">
                {ledger.data.strips} {ledger.data.strips === 1 ? "strip" : "strips"} read.
                {ledger.data.more && " Older strips are not in this list."}
              </p>
            </>
          )}
        </div>
      </Section>

      <Toast notice={job.notice} onDismiss={job.dismiss} />
    </main>
  );
}
