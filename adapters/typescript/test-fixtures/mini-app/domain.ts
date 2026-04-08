export interface IRepository {
  findById(id: string): Entity | null;
}

export class Entity {
  constructor(public readonly id: string, public name: string) {}
}
