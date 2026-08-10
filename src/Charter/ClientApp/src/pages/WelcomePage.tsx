import { useCallback, useState } from 'react';
import { useNavigate } from 'react-router';
import { useApi } from '@/api/api-context';
import type { TeachingLevel } from '@/api/types';
import { useViewer } from '@/app/viewer-context';
import { CharterMark } from '@/components/CharterMark';
import { SourceFooter } from '@/components/SourceFooter';
import { Button } from '@/components/ui/Button';
import { Card } from '@/components/ui/Card';
import { Icon } from '@/components/ui/Icon';
import { Markdown } from '@/components/ui/Markdown';
import { useAsync } from '@/hooks/useAsync';
import { cn } from '@/lib/cn';

const TEACHING: { value: TeachingLevel; label: string; description: string }[] = [
  {
    value: 'explain_everything',
    label: 'Explain everything',
    description: 'Define the words as they come up. Assume nothing.',
  },
  {
    value: 'skip_the_basics',
    label: 'Skip the basics',
    description: 'I know what a database and a deploy are. Tell me the reasoning.',
  },
  {
    value: 'just_the_decisions',
    label: 'Just the decisions',
    description: 'Trade-offs and alternatives only. Spare me the mechanics.',
  },
];

/**
 * §30.4, requester onboarding: "the highest-stakes and shortest. Three screens maximum."
 *
 * 1. What this is, plus the repo primer.
 * 2. How much explaining do you want — the §13 calibration, asked once and changeable later.
 * 3. A practice request.
 *
 * §30.4 is emphatic about the third: "the practice request matters more than anything else here. A
 * requester who has completed the full loop once, including clicking a preview, will file real
 * requests. One who has only read about it will not." So the last screen's only job is to get them
 * into the intake box, and the whole flow is skippable rather than modal — nothing here traps
 * someone who arrived because a colleague sent them a link.
 */
export function WelcomePage() {
  const api = useApi();
  const navigate = useNavigate();
  const { viewer, updatePreferences, completeOnboarding } = useViewer();
  const [step, setStep] = useState(0);
  const load = useCallback((signal: AbortSignal) => api.listProjects(signal), [api]);
  const projects = useAsync(load);
  const firstProject = projects.status === 'ready' ? projects.data[0] : undefined;

  const finish = (destination: string) => {
    void completeOnboarding().finally(() => {
      void navigate(destination);
    });
  };

  return (
    <div className="flex min-h-dvh flex-col">
      <main className="mx-auto flex w-full max-w-2xl grow flex-col justify-center px-4 py-10 sm:px-6">
        <div className="mb-6 flex items-center gap-2.5">
          <CharterMark size={26} />
          <span className="font-display text-title text-ink">Charter</span>
        </div>

        {step === 0 ? (
          <section>
            <h1 className="font-display text-hero text-ink">
              Ask for changes in your own words
            </h1>
            <p className="text-ink-muted text-lead mt-3">
              Describe what is wrong or what you need, the way you would say it to a colleague.
              Charter asks the questions a good project manager would ask, writes down what you both
              agreed, and only then has anything built. At the end you get something you can click
              and judge for yourself — no code, no GitHub, nothing to install.
            </p>

            {firstProject?.primerMd ? (
              <Card className="mt-6 px-4 py-4 sm:px-5">
                <h2 className="text-small text-ink font-medium">About {firstProject.name}</h2>
                <div className="text-small text-ink-muted mt-2">
                  <Markdown>{firstProject.primerMd}</Markdown>
                </div>
              </Card>
            ) : null}

            <div className="mt-7 flex flex-wrap items-center gap-3">
              <Button
                onClick={() => {
                  setStep(1);
                }}
                size="lg"
                variant="primary"
              >
                Next
                <Icon name="arrowRight" size={17} />
              </Button>
              <Button
                onClick={() => {
                  finish('/requests');
                }}
                variant="ghost"
              >
                Skip this
              </Button>
            </div>
          </section>
        ) : null}

        {step === 1 ? (
          <section>
            <h1 className="font-display text-hero text-ink">How much explaining do you want?</h1>
            <p className="text-ink-muted text-lead mt-3">
              After each change, Charter can tell you what it did and why. Pick whatever sounds
              right — you can change it any time, and it stops repeating things it has already
              explained to you.
            </p>

            <ul className="mt-6 space-y-2">
              {TEACHING.map((option) => {
                const selected = viewer.preferences.teachingLevel === option.value;
                return (
                  <li key={option.value}>
                    <button
                      aria-pressed={selected}
                      className={cn(
                        'w-full rounded-control border px-4 py-3.5 text-left transition-colors',
                        selected
                          ? 'border-accent-line bg-accent-soft'
                          : 'border-line hover:border-line-strong',
                      )}
                      onClick={() => {
                        void updatePreferences({ teachingLevel: option.value });
                      }}
                      type="button"
                    >
                      <span className="text-ink block font-medium">{option.label}</span>
                      <span className="text-small text-ink-muted block">{option.description}</span>
                    </button>
                  </li>
                );
              })}
            </ul>

            <div className="mt-7 flex flex-wrap items-center gap-3">
              <Button
                onClick={() => {
                  setStep(2);
                }}
                size="lg"
                variant="primary"
              >
                Next
                <Icon name="arrowRight" size={17} />
              </Button>
              <Button
                onClick={() => {
                  setStep(0);
                }}
                variant="ghost"
              >
                Back
              </Button>
            </div>
          </section>
        ) : null}

        {step === 2 ? (
          <section>
            <h1 className="font-display text-hero text-ink">Try one for real</h1>
            <p className="text-ink-muted text-lead mt-3">
              The fastest way to understand this is to do it once. Think of something small that
              annoys you — a button with the wrong words on it is perfect — and ask for it. You will
              be asked a couple of questions, shown a written plan to approve, and then given
              something to click.
            </p>
            <p className="text-ink-muted mt-3">
              Nothing gets built until you approve the plan, and nothing you do here can break the
              real thing.
            </p>

            <div className="mt-7 flex flex-wrap items-center gap-3">
              <Button
                onClick={() => {
                  finish('/requests/new');
                }}
                size="lg"
                variant="primary"
              >
                Write my first request
                <Icon name="arrowRight" size={17} />
              </Button>
              <Button
                onClick={() => {
                  finish('/requests');
                }}
                variant="ghost"
              >
                Later
              </Button>
            </div>
          </section>
        ) : null}

        <ol className="mt-10 flex gap-1.5" aria-label={`Step ${step + 1} of 3`}>
          {[0, 1, 2].map((index) => (
            <li
              className={cn(
                'h-1 w-8 rounded-full',
                index <= step ? 'bg-accent' : 'bg-line',
              )}
              key={index}
            />
          ))}
        </ol>
      </main>

      <SourceFooter />
    </div>
  );
}
