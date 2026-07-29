/**
 * The block vocabulary every generated guide page is written in.
 *
 * The agent and tuning guides share one page template each, so their bodies have to be data rather
 * than markup. Keeping the vocabulary this small is deliberate: a guide that needs an element not
 * in this list is usually a guide that has drifted away from the others, and the drift is the thing
 * worth noticing.
 *
 * `html` in `p`, `note`, and table cells is rendered unescaped so prose can carry `<code>` and
 * links. Everything here is authored in this repository — none of it is user input.
 */

export type Block =
  | { kind: "h"; text: string }
  | { kind: "p"; html: string }
  | { kind: "note"; html: string }
  | { kind: "code"; lang?: string; caption?: string; text: string }
  | { kind: "list"; items: string[] }
  | { kind: "table"; head: string[]; rows: string[][] };

export const h = (text: string): Block => ({ kind: "h", text });
export const p = (html: string): Block => ({ kind: "p", html });
export const note = (html: string): Block => ({ kind: "note", html });
export const code = (text: string, caption?: string, lang?: string): Block => ({
  kind: "code",
  text,
  caption,
  lang,
});
export const list = (...items: string[]): Block => ({ kind: "list", items });
export const table = (head: string[], rows: string[][]): Block => ({ kind: "table", head, rows });

export interface Source {
  label: string;
  url: string;
}

/** A question and its answer, rendered as prose *and* as FAQPage JSON-LD on the same page. */
export interface Faq {
  q: string;
  a: string;
}

export function faqJsonLd(faqs: Faq[]): Record<string, unknown> {
  return {
    "@type": "FAQPage",
    mainEntity: faqs.map((f) => ({
      "@type": "Question",
      name: f.q,
      acceptedAnswer: { "@type": "Answer", text: f.a },
    })),
  };
}
