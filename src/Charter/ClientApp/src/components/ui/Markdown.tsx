import { Fragment, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

/**
 * A deliberately tiny Markdown renderer for the small amount of server-authored prose the requester
 * sees: `primer.md`, an artifact's `instructions_md`, a teaching annotation.
 *
 * It supports paragraphs, unordered lists, `**bold**`, `*italic*` and `` `code` ``, and nothing
 * else. Two reasons to hand-roll rather than pull in a parser:
 *
 * - It builds React elements. There is no `dangerouslySetInnerHTML` anywhere in this app, so a
 *   Markdown string that reached the client from a repo (`primer.md` is written in the *target*
 *   repo, i.e. outside Charter's trust boundary — §16) cannot inject markup.
 * - A full CommonMark implementation is a larger dependency than the feature deserves. When the
 *   walkthrough (§13) needs tables and links, swap this for one — behind this same component.
 */

function renderInline(text: string, keyPrefix: string): ReactNode[] {
  const out: ReactNode[] = [];
  const pattern = /(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`)/g;
  let lastIndex = 0;
  let match: RegExpExecArray | null;
  let index = 0;

  while ((match = pattern.exec(text)) !== null) {
    if (match.index > lastIndex) {
      out.push(text.slice(lastIndex, match.index));
    }
    const token = match[0];
    const key = `${keyPrefix}-${index}`;
    index += 1;

    if (token.startsWith('**')) {
      out.push(
        <strong className="text-ink font-semibold" key={key}>
          {token.slice(2, -2)}
        </strong>,
      );
    } else if (token.startsWith('`')) {
      out.push(
        <code
          className="border-line bg-sunken text-small rounded-[0.25rem] border px-1 py-0.5 font-mono"
          key={key}
        >
          {token.slice(1, -1)}
        </code>,
      );
    } else {
      out.push(<em key={key}>{token.slice(1, -1)}</em>);
    }
    lastIndex = pattern.lastIndex;
  }

  if (lastIndex < text.length) {
    out.push(text.slice(lastIndex));
  }

  return out;
}

export interface MarkdownProps {
  children: string;
  className?: string;
}

export function Markdown({ children, className }: MarkdownProps) {
  const blocks = children.trim().split(/\n{2,}/);

  return (
    <div className={cn('space-y-3', className)}>
      {blocks.map((block, blockIndex) => {
        const lines = block.split('\n');
        const isList = lines.every((line) => /^\s*[-*]\s+/.test(line));

        if (isList) {
          return (
            <ul className="space-y-1.5 pl-1" key={blockIndex}>
              {lines.map((line, lineIndex) => (
                <li className="flex gap-2.5" key={lineIndex}>
                  <span aria-hidden="true" className="text-ink-subtle select-none">
                    &bull;
                  </span>
                  <span>{renderInline(line.replace(/^\s*[-*]\s+/, ''), `${blockIndex}-${lineIndex}`)}</span>
                </li>
              ))}
            </ul>
          );
        }

        return (
          <p key={blockIndex}>
            {lines.map((line, lineIndex) => (
              <Fragment key={lineIndex}>
                {lineIndex > 0 ? <br /> : null}
                {renderInline(line, `${blockIndex}-${lineIndex}`)}
              </Fragment>
            ))}
          </p>
        );
      })}
    </div>
  );
}
