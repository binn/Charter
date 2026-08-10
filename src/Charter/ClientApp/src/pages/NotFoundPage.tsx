import { Link } from 'react-router';
import { EmptyState } from '@/components/ui/EmptyState';

export function NotFoundPage() {
  return (
    <EmptyState
      description="That address does not lead anywhere in Charter. It may have been a link to something that has since been cleaned up."
      icon="search"
      secondary={
        <Link className="text-accent underline underline-offset-4" to="/requests">
          Back to your requests
        </Link>
      }
      title="Nothing here"
    />
  );
}
