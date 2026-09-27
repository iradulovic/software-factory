import { ReleasePlansView } from "@/components/release-plans";

export default async function ReleasePlanPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <ReleasePlansView selectedId={id} />;
}
