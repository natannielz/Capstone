import { BuyerCatalog } from '../../modules/catalog/BuyerCatalog';

export const metadata = { title: 'Katalog kemasan | Unit Toko' };
export default function ShopPage() { return <BuyerCatalog returnPath="/shop" />; }
