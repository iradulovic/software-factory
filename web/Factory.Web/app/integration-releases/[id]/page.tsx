import { FactoryReleaseDetails } from "@/components/factory-releases";

export default async function IntegrationReleasePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <FactoryReleaseDetails id={id} />;
}
