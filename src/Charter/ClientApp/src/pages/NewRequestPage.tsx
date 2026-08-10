import { useCallback, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { useApi } from '@/api/api-context';
import type { Project, RequestTemplate } from '@/api/types';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/Button';
import { Card, SectionLabel } from '@/components/ui/Card';
import { Disclosure } from '@/components/ui/Disclosure';
import { EmptyState } from '@/components/ui/EmptyState';
import { Icon, type IconName } from '@/components/ui/Icon';
import { Markdown } from '@/components/ui/Markdown';
import { Skeleton } from '@/components/ui/Skeleton';
import { TextArea, TextInput } from '@/components/ui/Field';
import { useAsync } from '@/hooks/useAsync';
import { cn } from '@/lib/cn';

const TEMPLATE_ICONS: Record<NonNullable<RequestTemplate['icon']>, IconName> = {
  text: 'text',
  bug: 'alert',
  field: 'layout',
  layout: 'layout',
  export: 'download',
  access: 'key',
  generic: 'message',
};

/**
 * Request intake (§10 entry point, §8 templates).
 *
 * A box you can type anything into, and templates beside it. §8 is direct about why the templates
 * are here: "a requester picking 'change some text' instead of free-typing skips half the
 * refinement round-trips. Cheapest quality win available." A template with fields turns into a few
 * short prompts rather than a blank page — but free text stays the default, because the product
 * promise is that you describe the problem in your own words.
 */
export function NewRequestPage() {
  const api = useApi();
  const navigate = useNavigate();
  const load = useCallback((signal: AbortSignal) => api.listProjects(signal), [api]);
  const projects = useAsync(load);

  const [projectId, setProjectId] = useState<string | null>(null);
  const [templateId, setTemplateId] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [fieldValues, setFieldValues] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const available: Project[] = projects.status === 'ready' ? projects.data : [];
  const project = available.find((candidate) => candidate.id === projectId) ?? available[0];
  const template = project?.templates.find((candidate) => candidate.id === templateId) ?? null;

  const composed = useMemo(() => {
    if (!template?.fields) {
      return text;
    }
    return template.fields
      .map((field) => `${field.label}\n${(fieldValues[field.key] ?? '').trim()}`)
      .filter((block) => block.split('\n')[1] !== '')
      .join('\n\n');
  }, [template, fieldValues, text]);

  const ready = project !== undefined && composed.trim().length >= 10;

  const submit = () => {
    if (!project || !ready || submitting) {
      return;
    }
    setSubmitting(true);
    setFailure(null);
    api
      .createRequest({
        projectId: project.id,
        rawText: composed.trim(),
        ...(template ? { templateId: template.id } : {}),
      })
      .then((created) => navigate(`/requests/${created.id}`))
      .catch(() => {
        setFailure('Charter could not start this off. Nothing was sent — try again.');
        setSubmitting(false);
      });
  };

  if (projects.status === 'loading') {
    return <Skeleton className="h-64 w-full" />;
  }

  if (projects.status === 'ready' && available.length === 0) {
    // §30.5 again: no projects means nothing to file against, and the next action belongs to
    // somebody else. Say so rather than showing an empty dropdown.
    return (
      <EmptyState
        description="Nobody has given you access to a project yet. Whoever set up Charter for your team can add you — until then there is nothing to file a request against."
        icon="package"
        secondary={
          <Link className="text-accent underline underline-offset-4" to="/requests">
            Back to requests
          </Link>
        }
        title="No projects yet"
      />
    );
  }

  return (
    <>
      <PageHeader
        description="Describe the problem, not the solution. Charter will ask about anything it needs before a line of code is written."
        title="What do you need?"
      />

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_20rem]">
        <div className="space-y-5">
          {available.length > 1 ? (
            <div>
              <SectionLabel>Which one is this about?</SectionLabel>
              <div className="mt-2.5 flex flex-wrap gap-2">
                {available.map((candidate) => (
                  <button
                    aria-pressed={candidate.id === project?.id}
                    className={cn(
                      'text-small rounded-control border px-3 py-2 font-medium transition-colors',
                      candidate.id === project?.id
                        ? 'border-accent-line bg-accent-soft text-accent-soft-ink'
                        : 'border-line text-ink-muted hover:border-line-strong hover:text-ink',
                    )}
                    key={candidate.id}
                    onClick={() => {
                      setProjectId(candidate.id);
                      setTemplateId(null);
                    }}
                    type="button"
                  >
                    {candidate.name}
                  </button>
                ))}
              </div>
            </div>
          ) : null}

          {template?.fields ? (
            <Card className="space-y-4 px-4 py-5 sm:px-5">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <p className="text-ink font-medium">{template.name}</p>
                  <p className="text-small text-ink-muted">{template.description}</p>
                </div>
                <Button
                  onClick={() => {
                    setTemplateId(null);
                    setFieldValues({});
                  }}
                  size="sm"
                  variant="ghost"
                >
                  Type it out instead
                </Button>
              </div>

              {template.fields.map((field) =>
                field.multiline ? (
                  <TextArea
                    key={field.key}
                    label={field.label}
                    onChange={(event) => {
                      setFieldValues((current) => ({ ...current, [field.key]: event.target.value }));
                    }}
                    placeholder={field.placeholder ?? ''}
                    rows={3}
                    value={fieldValues[field.key] ?? ''}
                  />
                ) : (
                  <TextInput
                    key={field.key}
                    label={field.label}
                    onChange={(event) => {
                      setFieldValues((current) => ({ ...current, [field.key]: event.target.value }));
                    }}
                    placeholder={field.placeholder ?? ''}
                    value={fieldValues[field.key] ?? ''}
                  />
                ),
              )}
            </Card>
          ) : (
            <TextArea
              autoFocus
              hint="However you would say it to a colleague is exactly right. Charter will ask if anything is unclear."
              label="What do you want changed?"
              onChange={(event) => {
                setText(event.target.value);
              }}
              placeholder="e.g. every time I start a new quote it makes me pick Solar again, even though that is nearly always what it is"
              rows={7}
              value={text}
            />
          )}

          {failure ? (
            <p className="border-danger-line bg-danger-soft text-danger text-small rounded-control border px-3 py-2">
              {failure}
            </p>
          ) : null}

          <div className="flex flex-wrap items-center gap-3">
            <Button disabled={!ready || submitting} onClick={submit} size="lg" variant="primary">
              {submitting ? 'Starting…' : 'Start working it out'}
              <Icon name="arrowRight" size={17} />
            </Button>
            <p className="text-small text-ink-subtle">Nothing is built until you approve a plan.</p>
          </div>
        </div>

        <aside className="space-y-4">
          {project && project.templates.length > 0 ? (
            <Card className="px-4 py-4">
              <SectionLabel>Common ones</SectionLabel>
              <ul className="mt-3 space-y-1.5">
                {project.templates.map((candidate) => (
                  <li key={candidate.id}>
                    <button
                      className={cn(
                        'flex w-full items-start gap-2.5 rounded-control border px-3 py-2.5 text-left transition-colors',
                        candidate.id === templateId
                          ? 'border-accent-line bg-accent-soft'
                          : 'border-transparent hover:bg-sunken',
                      )}
                      onClick={() => {
                        setTemplateId(candidate.id === templateId ? null : candidate.id);
                        setFieldValues({});
                        if (candidate.prompt && !candidate.fields) {
                          setText(candidate.prompt);
                        }
                      }}
                      type="button"
                    >
                      <Icon
                        className="text-ink-subtle mt-0.5"
                        name={TEMPLATE_ICONS[candidate.icon ?? 'generic']}
                        size={16}
                      />
                      <span className="min-w-0">
                        <span className="text-small text-ink block font-medium">
                          {candidate.name}
                        </span>
                        <span className="text-tiny text-ink-muted block">
                          {candidate.description}
                        </span>
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            </Card>
          ) : null}

          {project?.primerMd ? (
            <Card className="px-4 py-4">
              <Disclosure summary={`About ${project.name}`}>
                <div className="text-small text-ink-muted">
                  <Markdown>{project.primerMd}</Markdown>
                </div>
              </Disclosure>
            </Card>
          ) : null}
        </aside>
      </div>
    </>
  );
}
